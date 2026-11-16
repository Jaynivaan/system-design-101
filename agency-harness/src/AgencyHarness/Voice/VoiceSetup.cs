using AgencyHarness.Infrastructure;

namespace AgencyHarness.Voice;

/// <summary>Builds the provider chains from configuration (Harness:Voice) and registers the voice endpoints.</summary>
public static class VoiceSetup
{
    public static void AddVoice(this WebApplicationBuilder builder)
    {
        var o = builder.Configuration.GetSection("Harness:Voice").Get<VoiceOptions>() ?? new VoiceOptions();
        builder.Services.AddSingleton(o);
        builder.Services.AddSingleton<StreamTokens>();
        builder.Services.AddHttpClient("voice", c => c.Timeout = TimeSpan.FromSeconds(30));
        builder.Services.AddSingleton(sp => Build(sp, o));
    }

    private static VoiceProviders Build(IServiceProvider sp, VoiceOptions o)
    {
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("voice");
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Voice");
        var clock = sp.GetRequiredService<TimeProvider>();
        CircuitBreaker Breaker() => new(3, TimeSpan.FromSeconds(30), clock);

        // Response model: your custom LLM first, then Claude.
        var models = new List<(IResponseModel, CircuitBreaker)>();
        if (o.CustomLlm is { BaseUrl.Length: > 0 }) models.Add((new OpenAiCompatibleResponseModel(http, o.CustomLlm), Breaker()));
        if (o.ClaudeFallback && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            models.Add((new ClaudeResponseModel(new Anthropic.AnthropicClient(), o.ClaudeModel), Breaker()));
        IResponseModel model = new FallbackResponseModel(models, TimeSpan.FromMilliseconds(o.FirstTokenTimeoutMs), log);

        var sttEngines = new Dictionary<string, ISpeechToText>();
        if (o.GeminiStt is not null) sttEngines["gemini"] = new GeminiSpeechToText(http, o.GeminiStt);
        if (o.Stt is { BaseUrl.Length: > 0 }) sttEngines["http"] = new HttpSpeechToText(http, o.Stt);
        var sttChain = o.SttOrder.Concat(["gemini", "http"]).Distinct().Where(sttEngines.ContainsKey)
            .Select(n => (sttEngines[n], Breaker())).ToList();
        ISpeechToText stt = sttChain.Count > 0 ? new FallbackSpeechToText(sttChain) : new UnconfiguredStt();

        var engines = new Dictionary<string, ITextToSpeech>();
        if (o.LocalTts is { BaseUrl.Length: > 0 }) engines["local"] = new OpenAiSpeechTextToSpeech(http, o.LocalTts);
        if (o.GeminiTts is not null) engines["gemini"] = new GeminiTextToSpeech(http, o.GeminiTts);
        var routed = o.TtsRouting.ToDictionary(r => r.Key, r => (ITextToSpeech)new FallbackTextToSpeech(
            r.Value.Where(engines.ContainsKey).Select(n => (engines[n], Breaker())).ToList()));

        ILanguageId? lid = o.LanguageId is { BaseUrl.Length: > 0 } ? new IndicLanguageId(http, o.LanguageId) : null;
        ICallControl? control = string.IsNullOrEmpty(o.Twilio.AccountSid) ? null : new TwilioCallControl(http, o.Twilio);
        return new VoiceProviders(stt, new LanguageRoutedTextToSpeech(routed), model, new HeuristicEmotionDetector(), lid, control);
    }

    private class UnconfiguredStt : ISpeechToText
    {
        public Task<SttResult> TranscribeAsync(PcmAudio u, string? l, CancellationToken ct) =>
            throw new ProviderUnavailableException("Speech-to-text is not configured (Harness:Voice:Stt).");
    }
}
