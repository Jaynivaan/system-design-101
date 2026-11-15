using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgencyHarness.Infrastructure;

namespace AgencyHarness.Voice;

// ───────────────────────── Contracts ─────────────────────────
// Every model sits behind one of these. Your own models are just more implementations.

public record SttResult(string Text, string? Language, double? LanguageConfidence = null);
public record EmotionResult(string Label, double Intensity);
public record ChatTurn(string Role, string Text);   // "user" | "assistant"
public record TurnHints(string Language, EmotionResult? Emotion, string? Style = null);

public interface ISpeechToText
{
    /// <summary>Transcribe one utterance. <paramref name="languageHint"/> is the call's current language.</summary>
    Task<SttResult> TranscribeAsync(PcmAudio utterance, string? languageHint, CancellationToken ct);
}

public interface IEmotionDetector
{
    Task<EmotionResult> DetectAsync(PcmAudio utterance, string transcript, string language, CancellationToken ct);
}

public interface IResponseModel
{
    string Name { get; }
    /// <summary>Stream the reply, token by token. Must stop promptly when <paramref name="ct"/> fires (barge-in).</summary>
    IAsyncEnumerable<string> RespondAsync(string systemPrompt, IReadOnlyList<ChatTurn> history, CancellationToken ct);
}

public interface ITextToSpeech
{
    Task<PcmAudio> SpeakAsync(string text, string language, string? style, CancellationToken ct);
}

public class ProviderUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

// ───────────────────────── Response models ─────────────────────────

public class OpenAiCompatibleOptions
{
    /// <summary>e.g. http://192.168.1.20:11434 (Ollama), http://host:8000 (vLLM), http://host:8080 (llama.cpp).</summary>
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string? ApiKey { get; set; }
    public int MaxTokens { get; set; } = 300;
    public double Temperature { get; set; } = 0.6;
}

/// <summary>
/// Your fine-tuned LLM, served by anything that speaks the OpenAI chat-completions API with
/// stream=true (Ollama, vLLM, llama.cpp server, LM Studio, TGI). Tokens are streamed so speech
/// can start after the first sentence.
/// </summary>
public class OpenAiCompatibleResponseModel(HttpClient http, OpenAiCompatibleOptions o, string name = "custom-llm") : IResponseModel
{
    public string Name => name;

    public async IAsyncEnumerable<string> RespondAsync(string systemPrompt, IReadOnlyList<ChatTurn> history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        messages.AddRange(history.Select(h => new { role = h.Role, content = h.Text }));
        var body = JsonSerializer.Serialize(new { model = o.Model, messages, stream = true, max_tokens = o.MaxTokens, temperature = o.Temperature });

        using var req = new HttpRequestMessage(HttpMethod.Post, o.BaseUrl.TrimEnd('/') + "/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(o.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException e) { throw new ProviderUnavailableException($"{name} unreachable: {e.Message}", e); }
        using var _ = resp;
        if (!resp.IsSuccessStatusCode) throw new ProviderUnavailableException($"{name} returned {(int)resp.StatusCode}.");

        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:")) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;
            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("delta", out var d) && d.TryGetProperty("content", out var c)
                    && c.ValueKind == JsonValueKind.String)
                    delta = c.GetString();
            }
            catch (JsonException) { /* skip keep-alives or malformed lines */ }
            if (!string.IsNullOrEmpty(delta)) yield return delta;
        }
    }
}

/// <summary>Claude as the fallback response model (or primary for languages your model doesn't cover).</summary>
public class ClaudeResponseModel(Anthropic.AnthropicClient client, string model) : IResponseModel
{
    public string Name => "claude";

    public async IAsyncEnumerable<string> RespondAsync(string systemPrompt, IReadOnlyList<ChatTurn> history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Claude needs alternating turns that start with the user; merge any runs.
        var merged = new List<ChatTurn>();
        foreach (var t in history)
        {
            if (merged.Count > 0 && merged[^1].Role == t.Role) merged[^1] = merged[^1] with { Text = merged[^1].Text + "\n" + t.Text };
            else merged.Add(t);
        }
        if (merged.Count == 0 || merged[0].Role != "user") merged.Insert(0, new ChatTurn("user", "(call connected)"));

        var p = new Anthropic.Models.Messages.MessageCreateParams
        {
            Model = model,
            MaxTokens = 1024,
            System = systemPrompt,
            // Live voice: keep thinking short so the first words arrive quickly.
            OutputConfig = new Anthropic.Models.Messages.OutputConfig { Effort = Anthropic.Models.Messages.Effort.Low },
            Messages = merged.Select(t => t.Role == "user"
                ? (Anthropic.Models.Messages.MessageParam)new Anthropic.Models.Messages.UserMessageParam { Content = t.Text }
                : new Anthropic.Models.Messages.AssistantMessageParam { Content = t.Text }).ToList(),
        };

        IAsyncEnumerator<Anthropic.Models.Messages.RawMessageStreamEvent> e;
        try { e = client.Messages.CreateStreaming(p, ct).GetAsyncEnumerator(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new ProviderUnavailableException("Claude unavailable: " + ex.Message, ex); }
        await using var _ = e;
        while (true)
        {
            bool more;
            try { more = await e.MoveNextAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { throw new ProviderUnavailableException("Claude stream failed: " + ex.Message, ex); }
            if (!more) yield break;
            if (e.Current.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                yield return text.Text;
        }
    }
}

/// <summary>
/// Tries each model in order. A model is skipped when its circuit is open, when it fails before
/// producing anything, or when its first token takes longer than <paramref name="firstTokenTimeout"/>.
/// Once a model has started talking we stay with it (we can't un-say words).
/// </summary>
public class FallbackResponseModel(IReadOnlyList<(IResponseModel Model, CircuitBreaker Breaker)> chain, TimeSpan firstTokenTimeout,
    ILogger log) : IResponseModel
{
    public string Name => string.Join(">", chain.Select(c => c.Model.Name));
    public string? LastUsed { get; private set; }

    public async IAsyncEnumerable<string> RespondAsync(string systemPrompt, IReadOnlyList<ChatTurn> history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var (model, breaker) in chain)
        {
            if (!breaker.TryAcquire()) { log.LogInformation("Skipping {Model}: circuit open", model.Name); continue; }

            using var firstTokenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            firstTokenCts.CancelAfter(firstTokenTimeout);
            var e = model.RespondAsync(systemPrompt, history, firstTokenCts.Token).GetAsyncEnumerator(firstTokenCts.Token);
            string? first = null;
            bool ok = false;
            try
            {
                if (await e.MoveNextAsync()) first = e.Current;
                ok = true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("{Model} too slow to start; falling back", model.Name);
            }
            catch (ProviderUnavailableException ex)
            {
                log.LogWarning("{Model} failed: {Error}; falling back", model.Name, ex.Message);
            }
            if (!ok) { breaker.RecordFailure(); await e.DisposeAsync(); continue; }

            breaker.RecordSuccess();
            LastUsed = model.Name;
            firstTokenCts.CancelAfter(Timeout.InfiniteTimeSpan);   // the rest of the stream may take longer
            try
            {
                if (first is not null) yield return first;
                while (await e.MoveNextAsync()) yield return e.Current;
            }
            finally { await e.DisposeAsync(); }
            yield break;
        }
        throw new ProviderUnavailableException("No response model available.");
    }
}

// ───────────────────────── Speech-to-text ─────────────────────────

public class HttpSttOptions
{
    /// <summary>e.g. http://192.168.1.20:8891 (the media-AI sidecar running IndicConformer).</summary>
    public string BaseUrl { get; set; } = "";
    public string Path { get; set; } = "/asr";
    /// <summary>"raw": WAV as the request body, language in the query (the Kadha media-AI sidecar).
    /// "multipart": WAV as a form file (faster-whisper style servers).</summary>
    public string Mode { get; set; } = "raw";
    /// <summary>Query parameter for the language in raw mode.</summary>
    public string LanguageQuery { get; set; } = "lang";
    /// <summary>Language to use when the call has none yet (IndicConformer needs one; it doesn't auto-detect).</summary>
    public string DefaultLanguage { get; set; } = "ml";
    /// <summary>Multipart field that carries the WAV file.</summary>
    public string FileField { get; set; } = "file";
    /// <summary>Form field for the language hint (empty = don't send one).</summary>
    public string LanguageField { get; set; } = "language";
    /// <summary>Sample rate the model wants; audio is resampled from 8 kHz before sending.</summary>
    public int SampleRate { get; set; } = 16000;
    /// <summary>JSON property names in the response.</summary>
    public string TextProperty { get; set; } = "text";
    public string LanguageProperty { get; set; } = "lang";
    public string ConfidenceProperty { get; set; } = "language_confidence";
    public string? ApiKey { get; set; }
}

/// <summary>Speech-to-text over HTTP: one WAV per utterance (works with IndicConformer, faster-whisper servers, etc.).</summary>
public class HttpSpeechToText(HttpClient http, HttpSttOptions o) : ISpeechToText
{
    public async Task<SttResult> TranscribeAsync(PcmAudio utterance, string? languageHint, CancellationToken ct)
    {
        var wav = Wav.Write(new PcmAudio(Pcm.Resample(utterance.Samples, utterance.SampleRate, o.SampleRate), o.SampleRate));
        var lang = string.IsNullOrEmpty(languageHint) ? o.DefaultLanguage : languageHint;
        var url = o.BaseUrl.TrimEnd('/') + o.Path;
        HttpContent content;
        if (o.Mode == "raw")
        {
            url += (url.Contains('?') ? "&" : "?") + o.LanguageQuery + "=" + Uri.EscapeDataString(lang);
            content = new ByteArrayContent(wav);
            content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        }
        else
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, o.FileField, "utterance.wav");
            if (!string.IsNullOrEmpty(o.LanguageField)) form.Add(new StringContent(lang), o.LanguageField);
            content = form;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (!string.IsNullOrEmpty(o.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException e) { throw new ProviderUnavailableException("Speech-to-text unreachable: " + e.Message, e); }
        using var _ = resp;
        if (!resp.IsSuccessStatusCode) throw new ProviderUnavailableException($"Speech-to-text returned {(int)resp.StatusCode}.");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        string Str(string prop) => root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        double? conf = root.TryGetProperty(o.ConfidenceProperty, out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;
        var detected = Str(o.LanguageProperty);
        return new SttResult(Str(o.TextProperty).Trim(), string.IsNullOrEmpty(detected) ? null : detected, conf);
    }
}

/// <summary>Tries each speech-to-text provider in order (e.g. your local IndicConformer, then a cloud one).</summary>
public class FallbackSpeechToText(IReadOnlyList<(ISpeechToText Stt, CircuitBreaker Breaker)> chain) : ISpeechToText
{
    public async Task<SttResult> TranscribeAsync(PcmAudio utterance, string? languageHint, CancellationToken ct)
    {
        foreach (var (stt, breaker) in chain)
        {
            if (!breaker.TryAcquire()) continue;
            try { var r = await stt.TranscribeAsync(utterance, languageHint, ct); breaker.RecordSuccess(); return r; }
            catch (ProviderUnavailableException) { breaker.RecordFailure(); }
        }
        throw new ProviderUnavailableException("No speech-to-text available.");
    }
}

// ───────────────────────── Text-to-speech ─────────────────────────

public class OpenAiSpeechOptions
{
    /// <summary>e.g. http://192.168.1.20:8880 (your voice_server sidecar) or https://api.openai.com.</summary>
    public string BaseUrl { get; set; } = "";
    public string Path { get; set; } = "/v1/audio/speech";
    public string Model { get; set; } = "";
    /// <summary>Voice per language code, e.g. { "ml": "...", "en": "..." }; "*" is the default.</summary>
    public Dictionary<string, string> Voices { get; set; } = new();
    /// <summary>Model per language when it differs (sent as "model").</summary>
    public Dictionary<string, string> Models { get; set; } = new();
    /// <summary>Engine per language, sent as "engine" (Kadha voice_server: kokoro | mms | parler).</summary>
    public Dictionary<string, string> Engines { get; set; } = new();
    public double Speed { get; set; } = 1.0;
    /// <summary>Send the emotion/style hint as "instructions" (only for servers that support it).</summary>
    public bool SendStyle { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>Send the language code as a "language" field (for servers that accept it).</summary>
    public bool SendLanguage { get; set; } = true;
}

/// <summary>Text-to-speech over the OpenAI-style /v1/audio/speech API (your voice_server, OpenAI, Kokoro-FastAPI…).</summary>
public class OpenAiSpeechTextToSpeech(HttpClient http, OpenAiSpeechOptions o) : ITextToSpeech
{
    public async Task<PcmAudio> SpeakAsync(string text, string language, string? style, CancellationToken ct)
    {
        string Pick(Dictionary<string, string> map, string fallback) =>
            map.TryGetValue(language, out var v) ? v : map.TryGetValue("*", out var d) ? d : fallback;
        var payload = new Dictionary<string, object?>
        {
            ["model"] = Pick(o.Models, o.Model),
            ["input"] = text,
            ["voice"] = Pick(o.Voices, "default"),
            ["response_format"] = "wav",
        };
        if (o.SendLanguage) payload["language"] = language;
        var engine = Pick(o.Engines, "");
        if (!string.IsNullOrEmpty(engine)) payload["engine"] = engine;
        if (Math.Abs(o.Speed - 1.0) > 0.001) payload["speed"] = o.Speed;
        if (o.SendStyle && !string.IsNullOrEmpty(style)) payload["instructions"] = style;

        using var req = new HttpRequestMessage(HttpMethod.Post, o.BaseUrl.TrimEnd('/') + o.Path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(o.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException e) { throw new ProviderUnavailableException("Text-to-speech unreachable: " + e.Message, e); }
        using var _ = resp;
        if (!resp.IsSuccessStatusCode) throw new ProviderUnavailableException($"Text-to-speech returned {(int)resp.StatusCode}.");
        return Wav.Read(await resp.Content.ReadAsByteArrayAsync(ct));
    }
}

public class FallbackTextToSpeech(IReadOnlyList<(ITextToSpeech Tts, CircuitBreaker Breaker)> chain) : ITextToSpeech
{
    public async Task<PcmAudio> SpeakAsync(string text, string language, string? style, CancellationToken ct)
    {
        foreach (var (tts, breaker) in chain)
        {
            if (!breaker.TryAcquire()) continue;
            try { var r = await tts.SpeakAsync(text, language, style, ct); breaker.RecordSuccess(); return r; }
            catch (ProviderUnavailableException) { breaker.RecordFailure(); }
        }
        throw new ProviderUnavailableException("No text-to-speech available.");
    }
}

// ───────────────────────── Emotion ─────────────────────────

/// <summary>
/// Default emotion detector: cheap cues from the transcript and loudness. A real speech-emotion
/// model plugs in behind the same interface; this one keeps the escalation rules working meanwhile.
/// </summary>
public class HeuristicEmotionDetector : IEmotionDetector
{
    private static readonly string[] Distress = ["emergency", "accident", "hospital", "ambulance", "help me", "urgent", "അപകടം", "ആശുപത്രി", "അടിയന്തര", "സഹായിക്കണം", "urgence", "hôpital"];
    private static readonly string[] Anger = ["ridiculous", "unacceptable", "angry", "terrible", "worst", "fed up", "നാണക്കേട്", "ദേഷ്യം", "inacceptable", "furieux"];
    private static readonly string[] Happy = ["thank", "great", "perfect", "wonderful", "നന്ദി", "സന്തോഷം", "merci", "parfait"];

    public Task<EmotionResult> DetectAsync(PcmAudio utterance, string transcript, string language, CancellationToken ct)
    {
        var t = transcript.ToLowerInvariant();
        double loud = Math.Clamp((Pcm.LevelDb(utterance.Samples) + 30) / 20, 0, 1);   // -30 dBFS ≈ 0, -10 dBFS ≈ 1
        if (Distress.Any(t.Contains)) return Task.FromResult(new EmotionResult("distressed", Math.Max(0.7, loud)));
        if (Anger.Any(t.Contains) || t.Count(ch => ch == '!') >= 2) return Task.FromResult(new EmotionResult("angry", Math.Max(0.6, loud)));
        if (Happy.Any(t.Contains)) return Task.FromResult(new EmotionResult("happy", 0.5));
        return Task.FromResult(new EmotionResult("calm", 0.3));
    }
}
