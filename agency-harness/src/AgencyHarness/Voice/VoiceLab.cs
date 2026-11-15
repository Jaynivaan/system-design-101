using System.Text.RegularExpressions;
using AgencyHarness.Api;

namespace AgencyHarness.Voice;

public class VoiceLabOptions
{
    /// <summary>Folder of sample WAVs to audition (in the container: the /data volume).</summary>
    public string SamplesDir { get; set; } = "voice-samples";
    public int MaxPreviewChars { get; set; } = 500;
}

/// <summary>Speaks one line for the Voice lab. Separate from live calls, so you can audition voices
/// even while phone calls aren't set up.</summary>
public interface IVoicePreview
{
    IReadOnlyList<string> Engines { get; }
    Task<PcmAudio> SpeakAsync(string engine, string text, string language, string? voice, string? style, CancellationToken ct);
}

public class VoicePreview(IHttpClientFactory http, VoiceOptions voice) : IVoicePreview
{
    public IReadOnlyList<string> Engines =>
        new[] { "gemini", voice.LocalTts is { BaseUrl.Length: > 0 } ? "local" : null }.OfType<string>().ToList();

    public Task<PcmAudio> SpeakAsync(string engine, string text, string language, string? voiceName, string? style, CancellationToken ct)
    {
        var client = http.CreateClient("voice");
        if (engine == "local" && voice.LocalTts is { BaseUrl.Length: > 0 } local)
        {
            var o = Clone(local);
            if (!string.IsNullOrEmpty(voiceName)) o.Voices = new() { ["*"] = voiceName };
            return new OpenAiSpeechTextToSpeech(client, o).SpeakAsync(text, language, style, ct);
        }
        var g = voice.GeminiTts ?? new GeminiTtsOptions();
        var go = new GeminiTtsOptions { BaseUrl = g.BaseUrl, Model = g.Model, ApiKey = g.ApiKey, Voices = new(g.Voices) };
        if (!string.IsNullOrEmpty(voiceName)) go.Voices = new() { ["*"] = voiceName };
        return new GeminiTextToSpeech(client, go).SpeakAsync(text, language, style, ct);
    }

    private static OpenAiSpeechOptions Clone(OpenAiSpeechOptions s) => new()
    {
        BaseUrl = s.BaseUrl, Path = s.Path, Model = s.Model, Voices = new(s.Voices), Models = new(s.Models), Engines = new(s.Engines),
        ApiKey = s.ApiKey, SendLanguage = s.SendLanguage, Speed = s.Speed, SendStyle = true,
    };
}

public record PreviewRequest(string Text, string? Language, string? Voice, string? Style, string? Engine);

public static partial class VoiceLabEndpoints
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,120}\.wav$")]
    private static partial Regex SafeName();

    public static void AddVoiceLab(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(builder.Configuration.GetSection("Harness:VoiceLab").Get<VoiceLabOptions>() ?? new VoiceLabOptions());
        builder.Services.AddSingleton<IVoicePreview, VoicePreview>();
    }

    public static void MapVoiceLab(this RouteGroupBuilder v1)
    {
        // Sample clips (e.g. the Gemini voice samples made on your PC), newest first.
        v1.MapGet("/voice/samples", (VoiceLabOptions o) =>
        {
            var dir = new DirectoryInfo(o.SamplesDir);
            if (!dir.Exists) return Results.Ok(Array.Empty<object>());
            return Results.Ok(dir.EnumerateFiles("*.wav", SearchOption.TopDirectoryOnly)
                .Where(f => SafeName().IsMatch(f.Name))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new { name = f.Name, sizeBytes = f.Length, modified = f.LastWriteTimeUtc }));
        });

        v1.MapGet("/voice/samples/{name}", (string name, VoiceLabOptions o) =>
        {
            // Only plain file names from the samples folder: no paths, no other file types.
            if (!SafeName().IsMatch(name)) return Results.NotFound();
            var path = Path.Combine(Path.GetFullPath(o.SamplesDir), name);
            return File.Exists(path) ? Results.File(path, "audio/wav", enableRangeProcessing: true) : Results.NotFound();
        });

        v1.MapGet("/voice/engines", (IVoicePreview p) => Results.Ok(new { engines = p.Engines }));

        // Speak any line with a chosen engine, voice and tone. Each call uses the TTS provider's quota.
        v1.MapPost("/voice/preview", async (PreviewRequest req, IVoicePreview p, VoiceLabOptions o, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(req.Text)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = ["Required."] });
            if (req.Text.Length > o.MaxPreviewChars) return Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = [$"Max {o.MaxPreviewChars} characters."] });
            var engine = string.IsNullOrEmpty(req.Engine) ? "gemini" : req.Engine;
            if (!p.Engines.Contains(engine)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["engine"] = [$"Available: {string.Join(", ", p.Engines)}."] });
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var audio = await p.SpeakAsync(engine, req.Text.Trim(), req.Language ?? "ml", req.Voice, string.IsNullOrWhiteSpace(req.Style) ? null : req.Style.Trim(), http.RequestAborted);
                http.Response.Headers["X-Synthesis-Ms"] = ((int)sw.ElapsedMilliseconds).ToString();
                http.Response.Headers["X-Audio-Seconds"] = audio.Seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                return Results.File(Wav.Write(audio), "audio/wav");
            }
            catch (ProviderUnavailableException e)
            {
                return Results.Problem(e.Message, statusCode: 503);
            }
        });
    }
}
