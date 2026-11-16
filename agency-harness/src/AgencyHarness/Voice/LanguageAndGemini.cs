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
    /// <summary>Other TTS models to try when the main one is rate-limited or down (each has its own quota).</summary>
    public string[] FallbackModels { get; set; } = [];
    /// <summary>Longest wait for a rate limit to clear before trying the next model.</summary>
    public int MaxRetryWaitMs { get; set; } = 1500;
    /// <summary>Send the speaking style as a lead-in. Turn off if a model reads the style aloud.</summary>
    public bool UseStyle { get; set; } = true;
}

/// <summary>
/// Gemini text-to-speech (Malayalam and many more). Style and emotion are given as one short lead-in
/// ("Say gently: …"), which is how emotional tone reaches the voice.
/// Returns raw 16-bit PCM; the rate is read from the response's mime type (24 kHz typically).
/// </summary>
public class GeminiTextToSpeech(HttpClient http, GeminiTtsOptions o) : ITextToSpeech
{
    /// <summary>Gemini's documented form: one short lead-in clause, a colon, then the words ("Say gently: …").
    /// The style must not end in a full stop ("Say gently.: …" gets read aloud), and long meta-instructions are
    /// read aloud too, so nothing else is added.</summary>
    public static string BuildPrompt(string text, string? style)
    {
        style = style?.Trim().TrimEnd('.', ':', ';', ',', '!', ' ');
        return string.IsNullOrEmpty(style) ? text : $"{style}: {text}";
    }

    public async Task<PcmAudio> SpeakAsync(string text, string language, string? style, CancellationToken ct)
    {
        var key = string.IsNullOrEmpty(o.ApiKey) ? Environment.GetEnvironmentVariable("GEMINI_API_KEY") : o.ApiKey;
        if (string.IsNullOrEmpty(key)) throw new ProviderUnavailableException("Gemini TTS has no API key (set GEMINI_API_KEY).");
        var voice = o.Voices.TryGetValue(language, out var v) ? v : o.Voices.GetValueOrDefault("*", "Kore");
        var prompt = BuildPrompt(text, o.UseStyle ? style : null);
        var body = JsonSerializer.Serialize(new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new
            {
                responseModalities = new[] { "AUDIO" },
                speechConfig = new { voiceConfig = new { prebuiltVoiceConfig = new { voiceName = voice } } },
            },
        });
        // Main model, a short retry if it's rate-limited, then the fallback models (each has its own quota).
        var attempts = new List<string> { o.Model, o.Model };
        attempts.AddRange(o.FallbackModels);
        HttpResponseMessage? resp = null;
        string? lastError = null;
        for (int i = 0; i < attempts.Count; i++)
        {
            if (i == 1 && lastError != "429") continue;   // the same-model retry is only for rate limits
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.BaseUrl.TrimEnd('/')}/v1beta/models/{attempts[i]}:generateContent")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-goog-api-key", key);
            try { resp = await http.SendAsync(req, ct); }
            catch (HttpRequestException e) { lastError = e.Message; continue; }
            if (resp.IsSuccessStatusCode) break;
            lastError = ((int)resp.StatusCode).ToString();
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && i == 0)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(700);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(wait.TotalMilliseconds, o.MaxRetryWaitMs)), ct);
            }
            resp.Dispose(); resp = null;
        }
        if (resp is null) throw new ProviderUnavailableException($"Gemini TTS unavailable ({lastError}).");
        using var _ = resp;

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

public class GeminiSttOptions
{
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com";
    /// <summary>A fast multimodal Gemini model that accepts audio.</summary>
    public string Model { get; set; } = "gemini-3.8-flash";
    /// <summary>Languages the caller may speak; Gemini picks one and transcribes in its script.</summary>
    public string[] Languages { get; set; } = ["ml", "en", "fr", "hi", "ta"];
    public string? ApiKey { get; set; }
}

/// <summary>
/// Speech-to-text and language ID in one call, using Gemini's audio understanding. A cloud stand-in
/// for your local IndicConformer while the harness can't reach your PC's models; it also detects
/// English, which IndicConformer can't.
/// </summary>
public class GeminiSpeechToText(HttpClient http, GeminiSttOptions o) : ISpeechToText
{
    public async Task<SttResult> TranscribeAsync(PcmAudio utterance, string? languageHint, CancellationToken ct)
    {
        var key = string.IsNullOrEmpty(o.ApiKey) ? Environment.GetEnvironmentVariable("GEMINI_API_KEY") : o.ApiKey;
        if (string.IsNullOrEmpty(key)) throw new ProviderUnavailableException("Gemini speech-to-text has no API key (set GEMINI_API_KEY).");
        var wav = Wav.Write(new PcmAudio(Pcm.Resample(utterance.Samples, utterance.SampleRate, 16000), 16000));
        var langs = string.Join(", ", o.Languages.Select(l => $"{l} ({LanguageNames.Get(l)})"));
        var instruction =
            $"This is one caller utterance from a phone call. The caller most likely speaks {LanguageNames.Get(languageHint ?? o.Languages[0])}, " +
            $"but may speak any of: {langs}, or mix Malayalam with English. Transcribe exactly what was said, in the language's own script " +
            "(Malayalam in Malayalam script; keep English words that were spoken in English). Reply with only JSON: " +
            "{\"lang\": \"<code of the main language spoken>\", \"text\": \"<transcript>\"}. If there is no speech, use an empty text.";
        var body = JsonSerializer.Serialize(new
        {
            contents = new[] { new { parts = new object[] { new { text = instruction }, new { inline_data = new { mime_type = "audio/wav", data = Convert.ToBase64String(wav) } } } } },
            generationConfig = new { responseMimeType = "application/json", temperature = 0 },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.BaseUrl.TrimEnd('/')}/v1beta/models/{o.Model}:generateContent")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-goog-api-key", key);
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException e) { throw new ProviderUnavailableException("Gemini speech-to-text unreachable: " + e.Message, e); }
        using var _ = resp;
        if (!resp.IsSuccessStatusCode) throw new ProviderUnavailableException($"Gemini speech-to-text returned {(int)resp.StatusCode}.");
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var text = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
            text = text.Trim().Trim('`');
            if (text.StartsWith("json", StringComparison.OrdinalIgnoreCase)) text = text[4..];
            using var result = JsonDocument.Parse(text);
            var lang = result.RootElement.TryGetProperty("lang", out var l) ? l.GetString() : null;
            var transcript = result.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            if (lang is not null && !o.Languages.Contains(lang)) lang = null;
            return new SttResult(transcript.Trim(), lang, lang is null ? null : 0.9);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new ProviderUnavailableException("Gemini speech-to-text returned an unexpected answer.", e);
        }
    }
}
