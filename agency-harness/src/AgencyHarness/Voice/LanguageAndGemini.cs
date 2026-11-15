using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgencyHarness.Infrastructure;

namespace AgencyHarness.Voice;

public record LangResult(string Language, double Score);

public interface ILanguageId
{
    Task<LangResult?> DetectAsync(PcmAudio utterance, CancellationToken ct);
}

public class IndicLidOptions
{
    /// <summary>The media-AI sidecar running IndicConformer, e.g. http://172.24.224.1:8891.</summary>
    public string BaseUrl { get; set; } = "";
    public string Path { get; set; } = "/lid";
    /// <summary>Languages to choose between (IndicConformer codes). It can't detect English.</summary>
    public string[] Candidates { get; set; } = ["ml", "ta", "hi"];
    public int SampleRate { get; set; } = 16000;
}

/// <summary>Language ID per utterance via IndicConformer's /lid (raw WAV body, ?candidates=ml,ta).</summary>
public class IndicLanguageId(HttpClient http, IndicLidOptions o) : ILanguageId
{
    public async Task<LangResult?> DetectAsync(PcmAudio utterance, CancellationToken ct)
    {
        var wav = Wav.Write(new PcmAudio(Pcm.Resample(utterance.Samples, utterance.SampleRate, o.SampleRate), o.SampleRate));
        var url = $"{o.BaseUrl.TrimEnd('/')}{o.Path}?candidates={Uri.EscapeDataString(string.Join(',', o.Candidates))}";
        using var content = new ByteArrayContent(wav);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        HttpResponseMessage resp;
        try { resp = await http.PostAsync(url, content, ct); }
        catch (HttpRequestException e) { throw new ProviderUnavailableException("Language ID unreachable: " + e.Message, e); }
        using var _ = resp;
        if (!resp.IsSuccessStatusCode) throw new ProviderUnavailableException($"Language ID returned {(int)resp.StatusCode}.");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (!root.TryGetProperty("best", out var best) || best.ValueKind != JsonValueKind.String) return null;
        double score = 0;
        if (root.TryGetProperty("scores", out var scores) && scores.ValueKind == JsonValueKind.Array && scores.GetArrayLength() > 0
            && scores[0].TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number)
            score = sc.GetDouble();
        return new LangResult(best.GetString()!, score);
    }
}

public class GeminiTtsOptions
{
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com";
    /// <summary>The TTS model, e.g. the one you've been testing Malayalam samples with.</summary>
    public string Model { get; set; } = "gemini-3.8-flash-tts";
    /// <summary>Voice per language; "*" is the default.</summary>
    public Dictionary<string, string> Voices { get; set; } = new() { ["*"] = "Kore" };
    /// <summary>Read from the GEMINI_API_KEY environment variable when empty.</summary>
    public string? ApiKey { get; set; }
}

/// <summary>
/// Gemini text-to-speech (Malayalam and many more). Style and emotion are given in plain words
/// ("Say warmly and calmly: …"), which is how emotional tone reaches the voice.
/// Returns raw 16-bit PCM; the rate is read from the response's mime type (24 kHz typically).
/// </summary>
public class GeminiTextToSpeech(HttpClient http, GeminiTtsOptions o) : ITextToSpeech
{
    public async Task<PcmAudio> SpeakAsync(string text, string language, string? style, CancellationToken ct)
    {
        var key = string.IsNullOrEmpty(o.ApiKey) ? Environment.GetEnvironmentVariable("GEMINI_API_KEY") : o.ApiKey;
        if (string.IsNullOrEmpty(key)) throw new ProviderUnavailableException("Gemini TTS has no API key (set GEMINI_API_KEY).");
        var voice = o.Voices.TryGetValue(language, out var v) ? v : o.Voices.GetValueOrDefault("*", "Kore");
        var prompt = string.IsNullOrEmpty(style) ? text : $"{style}: {text}";
        var body = JsonSerializer.Serialize(new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new
            {
                responseModalities = new[] { "AUDIO" },
                speechConfig = new { voiceConfig = new { prebuiltVoiceConfig = new { voiceName = voice } } },
            },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.BaseUrl.TrimEnd('/')}/v1beta/models/{o.Model}:generateContent")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-goog-api-key", key);
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException e) { throw new ProviderUnavailableException("Gemini TTS unreachable: " + e.Message, e); }
        using var _ = resp;
        if (!resp.IsSuccessStatusCode) throw new ProviderUnavailableException($"Gemini TTS returned {(int)resp.StatusCode}.");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        try
        {
            var inline = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("inlineData");
            var data = Convert.FromBase64String(inline.GetProperty("data").GetString()!);
            var mime = inline.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : "";
            if (mime.Contains("wav")) return Wav.Read(data);
            int rate = 24000;
            var idx = mime.IndexOf("rate=", StringComparison.Ordinal);
            if (idx >= 0 && int.TryParse(new string(mime[(idx + 5)..].TakeWhile(char.IsDigit).ToArray()), out var r)) rate = r;
            var samples = new short[data.Length / 2];
            Buffer.BlockCopy(data, 0, samples, 0, samples.Length * 2);   // 16-bit little-endian PCM
            return new PcmAudio(samples, rate);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or FormatException)
        {
            throw new ProviderUnavailableException("Gemini TTS returned no audio.", e);
        }
    }
}

/// <summary>Chooses a text-to-speech chain per language, e.g. ml → Gemini; en → local Kokoro, then Gemini.</summary>
public class LanguageRoutedTextToSpeech(IReadOnlyDictionary<string, ITextToSpeech> byLanguage) : ITextToSpeech
{
    public Task<PcmAudio> SpeakAsync(string text, string language, string? style, CancellationToken ct)
    {
        var tts = byLanguage.TryGetValue(language, out var t) ? t
                : byLanguage.TryGetValue("*", out var d) ? d
                : throw new ProviderUnavailableException($"No text-to-speech configured for '{language}'.");
        return tts.SpeakAsync(text, language, style, ct);
    }
}

/// <summary>Decides when to switch the call's language, so one misheard word doesn't flip it.</summary>
public class LanguageTracker(string initial, double switchThreshold)
{
    public string Current { get; private set; } = initial;
    private string? _candidate;

    /// <summary>Returns true if the language changed.</summary>
    public bool Observe(LangResult? r)
    {
        if (r is null || r.Language == Current) { _candidate = null; return false; }
        // Switch at once on a confident result, or after two utterances in a row in the new language.
        if (r.Score >= switchThreshold || _candidate == r.Language)
        {
            Current = r.Language;
            _candidate = null;
            return true;
        }
        _candidate = r.Language;
        return false;
    }

    public void Force(string language) { Current = language; _candidate = null; }
}
