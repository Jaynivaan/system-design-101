using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgencyHarness.Infrastructure;
using AgencyHarness.Voice;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgencyHarness.Tests;

// ───────────── test doubles ─────────────

internal static class Tone
{
    /// <summary>20 ms μ-law frames: a 300 Hz tone (speech stand-in) or silence.</summary>
    public static IEnumerable<byte[]> Frames(int ms, bool speech, double amplitude = 9000)
    {
        for (int f = 0; f < ms / 20; f++)
        {
            var pcm = new short[160];
            if (speech) for (int i = 0; i < 160; i++) pcm[i] = (short)(amplitude * Math.Sin(2 * Math.PI * 300 * (f * 160 + i) / 8000.0));
            yield return G711.EncodeAll(pcm);
        }
    }

    public static PcmAudio Audio(double seconds, int rate = 24000) =>
        new(Enumerable.Range(0, (int)(seconds * rate)).Select(i => (short)(6000 * Math.Sin(2 * Math.PI * 220 * i / rate))).ToArray(), rate);
}

internal class FakeSink : IVoiceSink
{
    public int Frames;
    public int Clears;
    public readonly ConcurrentQueue<string> Marks = new();
    public Task SendAudioAsync(byte[] mulaw20ms, CancellationToken ct) { Interlocked.Increment(ref Frames); return Task.CompletedTask; }
    public Task ClearAsync(CancellationToken ct) { Interlocked.Increment(ref Clears); return Task.CompletedTask; }
    public Task MarkAsync(string name, CancellationToken ct) { Marks.Enqueue(name); return Task.CompletedTask; }
}

internal class FakeStt(Func<string> text) : ISpeechToText
{
    public int Calls;
    public string? LastLanguage;
    public Task<SttResult> TranscribeAsync(PcmAudio u, string? lang, CancellationToken ct)
    { Interlocked.Increment(ref Calls); LastLanguage = lang; return Task.FromResult(new SttResult(text(), lang)); }
}

internal class FakeTts(double seconds = 0.2) : ITextToSpeech
{
    public readonly ConcurrentQueue<(string Text, string Lang, string? Style)> Spoken = new();
    public Task<PcmAudio> SpeakAsync(string text, string lang, string? style, CancellationToken ct)
    { Spoken.Enqueue((text, lang, style)); return Task.FromResult(Tone.Audio(seconds)); }
}

internal class FakeModel(params string[] tokens) : IResponseModel
{
    public string Name => "fake";
    public string? LastPrompt;
    public bool Cancelled;
    public TimeSpan DelayPerToken = TimeSpan.Zero;
    public async IAsyncEnumerable<string> RespondAsync(string systemPrompt, IReadOnlyList<ChatTurn> history, [EnumeratorCancellation] CancellationToken ct)
    {
        LastPrompt = systemPrompt;
        var completed = false;
        try
        {
            foreach (var t in tokens)
            {
                try { if (DelayPerToken > TimeSpan.Zero) await Task.Delay(DelayPerToken, ct); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
                yield return t;
            }
            completed = true;
        }
        finally { if (!completed) Cancelled = true; }   // the consumer stopped early (e.g. barge-in while speaking)
    }
}

internal class FakeLid(Func<LangResult?> r) : ILanguageId
{
    public Task<LangResult?> DetectAsync(PcmAudio u, CancellationToken ct) => Task.FromResult(r());
}

internal class FakeControl : ICallControl
{
    public string? TransferredTo;
    public Task TransferAsync(string callId, string to, CancellationToken ct) { TransferredTo = to; return Task.CompletedTask; }
}

internal static class Wait
{
    public static async Task Until(Func<bool> cond, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > until) throw new TimeoutException("Condition not met in time."); await Task.Delay(20); }
    }
}

// ───────────── building blocks ─────────────

public class AudioTests
{
    [Fact]
    public void Mulaw_round_trip_is_close_and_silence_encodes_to_0xFF()
    {
        Assert.Equal(0xFF, G711.Encode(0));
        foreach (short s in new short[] { 100, -100, 1000, -5000, 12000, -32000, 32767, -32768 })
        {
            var back = G711.Decode(G711.Encode(s));
            Assert.InRange(Math.Abs(back - s), 0, Math.Max(8, Math.Abs((int)s) / 16));   // μ-law step is ~1/16 of magnitude
        }
    }

    [Fact]
    public void Wav_round_trips_and_resampling_keeps_duration()
    {
        var a = Tone.Audio(0.5, 24000);
        var back = Wav.Read(Wav.Write(a));
        Assert.Equal(24000, back.SampleRate);
        Assert.Equal(a.Samples, back.Samples);
        Assert.Equal(4000, Pcm.Resample(a.Samples, 24000, 8000).Length);
    }

    [Fact]
    public void Vad_finds_one_utterance_with_preroll_and_trims_trailing_silence()
    {
        var vad = new EnergyVad(new VadOptions());
        var events = new List<VadEvent>();
        PcmAudio? utt = null;
        foreach (var f in Tone.Frames(500, false).Concat(Tone.Frames(600, true)).Concat(Tone.Frames(800, false)))
        {
            var e = vad.Process(G711.DecodeAll(f), out var u);
            if (e != VadEvent.None) events.Add(e);
            utt ??= u;
        }
        Assert.Equal([VadEvent.SpeechStart, VadEvent.SpeechEnd], events);
        Assert.InRange(utt!.Seconds, 0.6, 1.2);   // speech + pre-roll, without the 600 ms of trailing silence
    }

    [Fact]
    public void Sentence_chunker_splits_latin_and_malayalam_and_flushes_the_rest()
    {
        var c = new SentenceChunker();
        var out1 = c.Push("നമസ്കാരം. How can").ToList();
        var out2 = c.Push(" I help? Tell me").ToList();
        Assert.Equal(["നമസ്കാരം."], out1);
        Assert.Equal(["How can I help?"], out2);
        Assert.Equal("Tell me", c.Flush());
    }

    [Fact]
    public void Language_tracker_needs_confidence_or_two_utterances_to_switch()
    {
        var t = new LanguageTracker("ml", 0.8);
        Assert.False(t.Observe(new LangResult("ta", 0.5)));   // one unsure "ta": stay
        Assert.False(t.Observe(new LangResult("ml", 0.9)));
        Assert.False(t.Observe(new LangResult("ta", 0.5)));
        Assert.True(t.Observe(new LangResult("ta", 0.5)));    // two in a row: switch
        Assert.True(t.Observe(new LangResult("hi", 0.95)));   // confident: switch at once
        Assert.Equal("hi", t.Current);
    }

    [Fact]
    public void Twilio_signature_and_stream_tokens_reject_tampering_and_expiry()
    {
        var form = new Dictionary<string, string> { ["CallSid"] = "CA1", ["From"] = "+19025550100" };
        var sig = TwilioSignature.Compute("secret", "https://x.test/voice/twilio/incoming", form);
        Assert.True(TwilioSignature.Verify("secret", "https://x.test/voice/twilio/incoming", form, sig));
        Assert.False(TwilioSignature.Verify("secret", "https://x.test/voice/twilio/incoming", new Dictionary<string, string> { ["CallSid"] = "CA2", ["From"] = "+19025550100" }, sig));
        Assert.False(TwilioSignature.Verify("other", "https://x.test/voice/twilio/incoming", form, sig));

        var tokens = new StreamTokens(new VoiceOptions());
        var now = DateTimeOffset.UtcNow;
        var tok = tokens.Issue("CA1", now);
        Assert.True(tokens.Validate(tok, "CA1", now));
        Assert.False(tokens.Validate(tok, "CA2", now));
        Assert.False(tokens.Validate(tok, "CA1", now.AddMinutes(5)));
    }

    [Fact]
    public async Task Fallback_model_skips_a_failing_or_slow_model()
    {
        var clock = TimeProvider.System;
        var failing = new ThrowingModel();
        var slow = new FakeModel("late") { DelayPerToken = TimeSpan.FromSeconds(5) };
        var good = new FakeModel("Hello", " there.");
        var chain = new FallbackResponseModel([(failing, new CircuitBreaker(3, TimeSpan.FromSeconds(30), clock)),
            (slow, new CircuitBreaker(3, TimeSpan.FromSeconds(30), clock)), (good, new CircuitBreaker(3, TimeSpan.FromSeconds(30), clock))],
            TimeSpan.FromMilliseconds(200), NullLogger.Instance);
        var text = new StringBuilder();
        await foreach (var t in chain.RespondAsync("sys", [], default)) text.Append(t);
        Assert.Equal("Hello there.", text.ToString());
        Assert.Equal("fake", chain.LastUsed);
    }

    private class ThrowingModel : IResponseModel
    {
        public string Name => "down";
        public async IAsyncEnumerable<string> RespondAsync(string s, IReadOnlyList<ChatTurn> h, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            throw new ProviderUnavailableException("offline");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}

// ───────────── a whole call ─────────────

public class CallSessionTests
{
    private static (CallSession S, FakeSink Sink, FakeTts Tts, FakeModel Model, FakeStt Stt, FakeControl Ctl) Make(
        string said = "എനിക്ക് ഒരു വെബ്സൈറ്റ് വേണം", FakeModel? model = null, FakeTts? tts = null, ILanguageId? lid = null)
    {
        var sink = new FakeSink();
        tts ??= new FakeTts();
        model ??= new FakeModel("ശരി, ഞാൻ സന്ദേശം എടുക്കാം. ", "നിങ്ങളുടെ പേര്?");
        var stt = new FakeStt(() => said);
        var ctl = new FakeControl();
        var o = new VoiceOptions { TransferNumber = "+19025550199" };
        var s = new CallSession("CA-test", sink, new VoiceProviders(stt, tts, model, new HeuristicEmotionDetector(), lid, ctl), o, NullLogger.Instance);
        return (s, sink, tts, model, stt, ctl);
    }

    private static void Say(CallSession s, int speechMs = 500, int silenceMs = 800)
    {
        foreach (var f in Tone.Frames(speechMs, true).Concat(Tone.Frames(silenceMs, false))) s.OnAudio(f);
    }

    private static void AckAllMarks(CallSession s, FakeSink sink) { while (sink.Marks.TryDequeue(out var m)) s.OnMark(m); }

    /// <summary>Wait until the greeting has been sent and played (its mark acknowledged) and the session is idle.</summary>
    private static async Task GreetingPlayed(CallSession s, FakeSink sink)
    {
        await Wait.Until(() => !sink.Marks.IsEmpty);
        AckAllMarks(s, sink);
        await Wait.Until(() => !s.IsSpeaking);
    }

    [Fact]
    public async Task Greets_then_answers_in_malayalam_sentence_by_sentence()
    {
        var (s, sink, tts, model, stt, _) = Make();
        await s.StartAsync();
        await GreetingPlayed(s, sink);
        Assert.Contains("നമസ്കാരം", tts.Spoken.First().Text);

        Say(s);
        await Wait.Until(() => tts.Spoken.Count == 3);
        await Wait.Until(() => !s.IsSpeaking || sink.Marks.Count == 2);
        Assert.Equal("ml", stt.LastLanguage);
        Assert.Contains("Malayalam", model.LastPrompt);
        Assert.Contains("Your name is Vaani", model.LastPrompt);
        Assert.Contains("വാണി", tts.Spoken.First().Text);
        Assert.Equal(["ശരി, ഞാൻ സന്ദേശം എടുക്കാം.", "നിങ്ങളുടെ പേര്?"], tts.Spoken.Skip(1).Select(x => x.Text));
        Assert.True(sink.Frames > 0);
        await s.DisposeAsync();
    }

    [Fact]
    public async Task Caller_talking_over_the_reply_clears_playback_and_cancels_generation()
    {
        var model = new FakeModel("First sentence here. ", "Second sentence. ", "Third.") { DelayPerToken = TimeSpan.FromMilliseconds(300) };
        var (s, sink, tts, _, _, _) = Make(model: model, tts: new FakeTts(seconds: 3));
        await s.StartAsync();
        await GreetingPlayed(s, sink);

        Say(s);
        await Wait.Until(() => tts.Spoken.Count == 2);           // first reply sentence is playing (mark not acked)
        Assert.True(s.IsSpeaking);
        foreach (var f in Tone.Frames(400, true)) s.OnAudio(f);  // caller interrupts
        await Wait.Until(() => sink.Clears == 1);
        await Wait.Until(() => model.Cancelled);
        Assert.False(s.IsSpeaking);
        Assert.True(s.Turns.Last().Interrupted);
        Assert.Equal("", s.Turns.Last().AssistantText);           // the caller heard none of it (no mark came back)
        await s.DisposeAsync();
    }

    [Fact]
    public async Task Quiet_echo_and_short_noises_while_vaani_speaks_dont_cut_her_off()
    {
        var model = new FakeModel("First sentence here. ", "Second sentence. ") { DelayPerToken = TimeSpan.FromMilliseconds(200) };
        var (s, sink, tts, _, _, _) = Make(model: model, tts: new FakeTts(seconds: 3));
        await s.StartAsync();
        await GreetingPlayed(s, sink);
        Say(s);
        await Wait.Until(() => tts.Spoken.Count == 2);
        Assert.True(s.IsSpeaking);

        foreach (var f in Tone.Frames(800, true, amplitude: 250)) s.OnAudio(f);   // faint echo of her own voice
        foreach (var f in Tone.Frames(160, true)) s.OnAudio(f);                   // a cough
        foreach (var f in Tone.Frames(700, false)) s.OnAudio(f);
        Assert.Equal(0, sink.Clears);
        Assert.False(model.Cancelled);

        foreach (var f in Tone.Frames(400, true)) s.OnAudio(f);                   // the caller really talks over her
        await Wait.Until(() => sink.Clears == 1);
        await s.DisposeAsync();
    }

    [Fact]
    public async Task Long_replies_are_spoken_in_a_few_chunks_not_one_request_per_sentence()
    {
        var model = new FakeModel("One. ", "Two is a bit longer than one. ", "Three keeps going for a while. ", "Four adds more detail here. ", "Five ends it.");
        var (s, sink, tts, _, _, _) = Make(model: model);
        await s.StartAsync();
        await GreetingPlayed(s, sink);
        Say(s);
        await Wait.Until(() => s.Turns.Count >= 2 && !string.IsNullOrEmpty(s.Turns.Last().AssistantText) && !s.IsSpeaking || sink.Marks.Count >= 2, 5000);
        await Wait.Until(() => tts.Spoken.Count >= 3);
        var reply = tts.Spoken.Skip(1).Select(x => x.Text).ToList();
        Assert.Equal("One.", reply[0]);                 // first sentence immediately
        Assert.Equal(2, reply.Count);                    // the other four in one chunk
        Assert.StartsWith("Two is", reply[1]);
        Assert.EndsWith("Five ends it.", reply[1]);
        await s.DisposeAsync();
    }

    [Fact]
    public async Task Switches_language_when_the_caller_does()
    {
        var langs = new Queue<LangResult?>([new LangResult("hi", 0.95)]);
        var (s, sink, tts, model, stt, _) = Make(said: "मुझे वेबसाइट चाहिए", lid: new FakeLid(() => langs.TryDequeue(out var r) ? r : null));
        await s.StartAsync();
        await GreetingPlayed(s, sink);
        Say(s);
        await Wait.Until(() => tts.Spoken.Count >= 2);
        Assert.Equal("hi", s.Language);
        Assert.Equal("hi", stt.LastLanguage);
        Assert.Contains("Hindi", model.LastPrompt);
        Assert.All(tts.Spoken.Skip(1), x => Assert.Equal("hi", x.Lang));
        await s.DisposeAsync();
    }

    [Fact]
    public async Task Distressed_caller_is_transferred_to_a_human_with_a_calm_voice()
    {
        var (s, sink, tts, model, _, ctl) = Make(said: "there has been an accident, please help me");
        await s.StartAsync();
        await GreetingPlayed(s, sink);
        Say(s);
        await Wait.Until(() => tts.Spoken.Count == 2);
        await Wait.Until(() => !sink.Marks.IsEmpty);   // the hand-off line's mark has been sent…
        AckAllMarks(s, sink);                           // …and is now "played", so the transfer can go ahead
        await Wait.Until(() => ctl.TransferredTo is not null);
        Assert.Equal("+19025550199", ctl.TransferredTo);
        var line = tts.Spoken.Last();
        Assert.Contains("ഒരു നിമിഷം", line.Text);                    // Malayalam "one moment…"
        Assert.Equal("Speak gently, calmly and reassuringly", line.Style);
        Assert.Null(model.LastPrompt);                                 // rules decided; the model wasn't needed
        await s.DisposeAsync();
    }
}

// ───────────── through the real Twilio endpoints ─────────────

public class TwilioEndpointTests
{
    private static HarnessFactory App(FakeTts tts, FakeModel model)
    {
        var app = new HarnessFactory();
        app.Settings["Harness:Voice:Enabled"] = "true";
        app.Settings["Harness:Voice:PublicBaseUrl"] = "http://localhost";
        app.Settings["Harness:Voice:Twilio:AuthToken"] = "twilio-test-token";
        app.ConfigureServices = s =>
        {
            s.RemoveAll<VoiceProviders>();
            s.AddSingleton(new VoiceProviders(new FakeStt(() => "hello"), tts, model, new HeuristicEmotionDetector(), null, null));
        };
        return app;
    }

    private static async Task<string> TwimlAsync(HarnessFactory app, string callSid, bool sign = true)
    {
        var form = new Dictionary<string, string> { ["CallSid"] = callSid, ["From"] = "+19025550100", ["To"] = "+19025550111" };
        var req = new HttpRequestMessage(HttpMethod.Post, "/voice/twilio/incoming") { Content = new FormUrlEncodedContent(form) };
        if (sign) req.Headers.Add("X-Twilio-Signature", TwilioSignature.Compute("twilio-test-token", "http://localhost/voice/twilio/incoming", form));
        var resp = await app.CreateClient().SendAsync(req);
        return resp.StatusCode == HttpStatusCode.OK ? await resp.Content.ReadAsStringAsync() : resp.StatusCode.ToString();
    }

    private static Task SendJson(WebSocket ws, object o) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(o), WebSocketMessageType.Text, true, CancellationToken.None);

    [Fact]
    public async Task Unsigned_webhook_is_rejected()
    {
        using var app = App(new FakeTts(), new FakeModel("Hi."));
        Assert.Equal("Forbidden", await TwimlAsync(app, "CA9", sign: false));
    }

    [Fact]
    public async Task Incoming_call_streams_greeting_and_reply_and_records_the_call()
    {
        var tts = new FakeTts(0.1);
        using var app = App(tts, new FakeModel("Sure, I can help."));
        var tenant = await app.NewTenantAsync();
        app.Services.GetRequiredService<VoiceOptions>().TenantId = tenant.TenantId;

        var twiml = await TwimlAsync(app, "CA100");
        Assert.Contains("<Stream url=\"ws://localhost/voice/twilio/stream\">", twiml);
        var token = System.Text.RegularExpressions.Regex.Match(twiml, "name=\"token\" value=\"([^\"]+)\"").Groups[1].Value;

        var ws = await app.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/voice/twilio/stream"), CancellationToken.None);
        await SendJson(ws, new { @event = "connected" });
        await SendJson(ws, new { @event = "start", streamSid = "MZ1", start = new { streamSid = "MZ1", callSid = "CA100", customParameters = new { token } } });

        var received = new ConcurrentQueue<JsonElement>();
        var reader = Task.Run(async () =>
        {
            var buf = new byte[1 << 16];
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) break;
                received.Enqueue(JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, r.Count)).RootElement.Clone());
            }
        });

        await Wait.Until(() => received.Any(m => m.GetProperty("event").GetString() == "mark"));   // greeting played
        foreach (var m in received.Where(m => m.GetProperty("event").GetString() == "mark"))
            await SendJson(ws, new { @event = "mark", streamSid = "MZ1", mark = new { name = m.GetProperty("mark").GetProperty("name").GetString() } });
        foreach (var f in Tone.Frames(500, true).Concat(Tone.Frames(800, false)))
            await SendJson(ws, new { @event = "media", streamSid = "MZ1", media = new { track = "inbound", payload = Convert.ToBase64String(f) } });

        await Wait.Until(() => tts.Spoken.Count == 2);
        await Wait.Until(() => received.Count(m => m.GetProperty("event").GetString() == "mark") == 2);
        var media = received.First(m => m.GetProperty("event").GetString() == "media");
        Assert.Equal("MZ1", media.GetProperty("streamSid").GetString());
        Assert.Equal(160, Convert.FromBase64String(media.GetProperty("media").GetProperty("payload").GetString()!).Length);

        await SendJson(ws, new { @event = "stop", streamSid = "MZ1" });
        await Wait.Until(() => ws.State != WebSocketState.Open || reader.IsCompleted, 3000).ContinueWith(_ => { });
        await Wait.Until(() =>
        {
            using var scope = app.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<HarnessDbContext>().Outbox.Any(o => o.EventType == "call.completed");
        });
    }

    [Fact]
    public async Task Media_stream_with_a_forged_token_is_closed()
    {
        using var app = App(new FakeTts(), new FakeModel("Hi."));
        var ws = await app.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/voice/twilio/stream"), CancellationToken.None);
        await SendJson(ws, new { @event = "start", streamSid = "MZ2", start = new { streamSid = "MZ2", callSid = "CA200", customParameters = new { token = "1.forged" } } });
        var buf = new byte[1024];
        var r = await ws.ReceiveAsync(buf, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, r.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, ws.CloseStatus);
    }
}


public class VoiceLabTests
{
    private class FakePreview : IVoicePreview
    {
        public (string Engine, string Text, string Lang, string? Voice, string? Style)? Last;
        public IReadOnlyList<string> Engines => ["gemini"];
        public Task<PcmAudio> SpeakAsync(string engine, string text, string language, string? voice, string? style, CancellationToken ct)
        { Last = (engine, text, language, voice, style); return Task.FromResult(Tone.Audio(1.5)); }
    }

    [Fact]
    public async Task Lists_and_serves_sample_clips_but_nothing_outside_the_folder()
    {
        var dir = Directory.CreateTempSubdirectory("voice-samples-").FullName;
        File.WriteAllBytes(Path.Combine(dir, "ml-L1-excited-Kore.wav"), Wav.Write(Tone.Audio(0.5)));
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "not audio");
        using var app = new HarnessFactory();
        app.Settings["Harness:VoiceLab:SamplesDir"] = dir;
        var (c, _) = await app.NewTenantAsync();

        var list = JsonDocument.Parse(await c.GetStringAsync("/v1/voice/samples")).RootElement;
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("ml-L1-excited-Kore.wav", list[0].GetProperty("name").GetString());

        var wav = await c.GetAsync("/v1/voice/samples/ml-L1-excited-Kore.wav");
        Assert.Equal("audio/wav", wav.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0.5, Wav.Read(await wav.Content.ReadAsByteArrayAsync()).Seconds, 2);

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/v1/voice/samples/notes.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/v1/voice/samples/..%2Fharness.db")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/v1/voice/samples")).StatusCode);
    }

    [Fact]
    public async Task Preview_speaks_a_line_with_the_chosen_voice_and_tone()
    {
        var fake = new FakePreview();
        using var app = new HarnessFactory { ConfigureServices = s => { s.RemoveAll<IVoicePreview>(); s.AddSingleton<IVoicePreview>(fake); } };
        var (c, _) = await app.NewTenantAsync();
        var resp = await c.PostAsync("/v1/voice/preview", new StringContent(
            JsonSerializer.Serialize(new { text = "നമസ്കാരം!", language = "ml", voice = "Aoede", style = "Say warmly" }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1.5, Wav.Read(await resp.Content.ReadAsByteArrayAsync()).Seconds, 1);
        Assert.Equal(("gemini", "നമസ്കാരം!", "ml", "Aoede", "Say warmly"), fake.Last);
        Assert.True(resp.Headers.Contains("X-Synthesis-Ms"));

        var tooLong = await c.PostAsync("/v1/voice/preview", new StringContent(
            JsonSerializer.Serialize(new { text = new string('x', 1001) }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }
}

public class GeminiAdapterTests
{
    private class Stub(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Last = r; LastBody = r.Content is null ? null : await r.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(r), Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Whisper_transcribes_detects_language_and_drops_invented_text_on_noise()
    {
        var stub = new Stub(_ => JsonSerializer.Serialize(new
        {
            language = "malayalam",
            text = "എനിക്ക് ഒരു വെബ്സൈറ്റ് വേണം Thank you for watching",
            segments = new object[]
            {
                new { text = " എനിക്ക് ഒരു വെബ്സൈറ്റ് വേണം", no_speech_prob = 0.05 },
                new { text = " Thank you for watching", no_speech_prob = 0.92 },
            },
        }));
        var stt = new HttpSpeechToText(new HttpClient(stub), new HttpSttOptions { BaseUrl = "http://whisper:8000", Mode = "openai", Model = "whisper-large-v3", ApiKey = "k" });
        var r = await stt.TranscribeAsync(new PcmAudio(new short[8000], 8000), "en", default);
        Assert.Equal("എനിക്ക് ഒരു വെബ്സൈറ്റ് വേണം", r.Text);
        Assert.Equal("ml", r.Language);
        Assert.Equal("/v1/audio/transcriptions", stub.Last!.RequestUri!.AbsolutePath);
        Assert.Contains("whisper-large-v3", stub.LastBody);
        Assert.Contains("verbose_json", stub.LastBody);
        Assert.DoesNotContain("name=language", stub.LastBody);   // no hint: Whisper detects, so callers can switch language
        Assert.Equal("Bearer", stub.Last.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task Gemini_stt_returns_transcript_and_detected_language()
    {
        var inner = JsonSerializer.Serialize(new { lang = "ml", text = "എനിക്ക് ഒരു website വേണം" });
        var stub = new Stub(_ => JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text = inner } } } } } }));
        var stt = new GeminiSpeechToText(new HttpClient(stub), new GeminiSttOptions { ApiKey = "k", Model = "m" });
        var r = await stt.TranscribeAsync(Tone.Audio(0.5, 8000), "ml", default);
        Assert.Equal("എനിക്ക് ഒരു website വേണം", r.Text);
        Assert.Equal("ml", r.Language);
        Assert.EndsWith("/v1beta/models/m:generateContent", stub.Last!.RequestUri!.AbsolutePath);
        Assert.Equal("k", stub.Last.Headers.GetValues("x-goog-api-key").Single());
        Assert.Contains("audio/wav", stub.LastBody);
    }

    [Fact]
    public async Task Gemini_stt_ignores_languages_outside_the_allowed_list()
    {
        var inner = JsonSerializer.Serialize(new { lang = "xx", text = "hello" });
        var stub = new Stub(_ => JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text = "```json\n" + inner + "\n```" } } } } } }));
        var r = await new GeminiSpeechToText(new HttpClient(stub), new GeminiSttOptions { ApiKey = "k" }).TranscribeAsync(Tone.Audio(0.3, 8000), null, default);
        Assert.Equal("hello", r.Text);
        Assert.Null(r.Language);
    }

    [Fact]
    public async Task Gemini_tts_retries_a_rate_limit_then_falls_back_to_another_model()
    {
        var pcm = new byte[4800];
        int calls = 0;
        var stub = new Stub(r =>
        {
            calls++;
            return r.RequestUri!.AbsolutePath.Contains("lite-tts")
                ? JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { inlineData = new { mimeType = "audio/L16;rate=24000", data = Convert.ToBase64String(pcm) } } } } } } })
                : "RATE";
        });
        var handler = new RateLimitingHandler(stub);
        var tts = new GeminiTextToSpeech(new HttpClient(handler), new GeminiTtsOptions { ApiKey = "k", Model = "main-tts", FallbackModels = ["main-lite-tts"], MaxRetryWaitMs = 10 });
        var audio = await tts.SpeakAsync("നമസ്കാരം", "ml", null, default);
        Assert.Equal(2400, audio.Samples.Length);
        Assert.Equal(["main-tts", "main-tts", "main-lite-tts"], handler.Models);   // try, retry once, then fall back
    }

    /// <summary>Turns the stub's "RATE" answers into real 429 responses.</summary>
    private class RateLimitingHandler(Stub inner) : DelegatingHandler(inner)
    {
        public readonly List<string> Models = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Models.Add(r.RequestUri!.AbsolutePath.Split('/').Last().Split(':')[0]);
            var resp = await base.SendAsync(r, ct);
            return await resp.Content.ReadAsStringAsync(ct) == "RATE" ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : resp;
        }
    }

    [Fact]
    public async Task Gemini_tts_reads_raw_pcm_with_rate_from_mime_type()
    {
        var pcm = new short[2400]; for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(i % 100);
        var bytes = new byte[pcm.Length * 2]; Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        var stub = new Stub(_ => JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { inlineData = new { mimeType = "audio/L16;codec=pcm;rate=24000", data = Convert.ToBase64String(bytes) } } } } } } }));
        var audio = await new GeminiTextToSpeech(new HttpClient(stub), new GeminiTtsOptions { ApiKey = "k" }).SpeakAsync("നമസ്കാരം", "ml", "Say warmly", default);
        Assert.Equal(24000, audio.SampleRate);
        Assert.Equal(pcm, audio.Samples);
        var sent = JsonDocument.Parse(stub.LastBody!).RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString();
        Assert.Equal("Say warmly: നമസ്കാരം", sent);
    }
}

public class WebVoiceTests
{
    private static HarnessFactory App(FakeTts tts, FakeModel model)
    {
        var app = new HarnessFactory();   // note: phone voice NOT enabled; browser talk works without Twilio
        app.ConfigureServices = s =>
        {
            s.RemoveAll<VoiceProviders>();
            s.AddSingleton(new VoiceProviders(new FakeStt(() => "എനിക്ക് ഒരു വെബ്സൈറ്റ് വേണം"), tts, model, new HeuristicEmotionDetector(), null, null));
        };
        return app;
    }

    private static Task Send(WebSocket ws, object o) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(o), WebSocketMessageType.Text, true, CancellationToken.None);

    private static Task Reader(WebSocket ws, ConcurrentQueue<JsonElement> into) => Task.Run(async () =>
    {
        var buf = new byte[1 << 16];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) break;
                into.Enqueue(JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, r.Count)).RootElement.Clone());
            }
        }
        catch (WebSocketException) { }
    });

    [Fact]
    public async Task Browser_talk_streams_audio_both_ways_with_a_live_transcript_and_records_the_conversation()
    {
        var tts = new FakeTts(0.1);
        using var app = App(tts, new FakeModel("ശരി! ", "എന്ത് തരം വെബ്സൈറ്റ്?"));
        var (c, tenantId) = await app.NewTenantAsync();
        var ticket = JsonDocument.Parse(await (await c.PostAsync("/v1/voice/web/ticket", null)).Content.ReadAsStringAsync()).RootElement;
        var id = ticket.GetProperty("id").GetString()!;
        var token = ticket.GetProperty("token").GetString()!;
        Assert.StartsWith($"web-{tenantId:N}-", id);

        var ws = await app.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/voice/web/stream"), CancellationToken.None);
        var got = new ConcurrentQueue<JsonElement>();
        var reader = Reader(ws, got);
        await Send(ws, new { @event = "start", streamSid = id, start = new { streamSid = id, callSid = id, customParameters = new { token } } });

        string Ev(JsonElement m) => m.GetProperty("event").GetString()!;
        await Wait.Until(() => got.Any(m => Ev(m) == "mark"));
        Assert.Contains(got, m => Ev(m) == "transcript" && m.GetProperty("role").GetString() == "assistant" && m.GetProperty("text").GetString()!.Contains("വാണി"));
        foreach (var m in got.Where(m => Ev(m) == "mark").ToList())
            await Send(ws, new { @event = "mark", streamSid = id, mark = new { name = m.GetProperty("mark").GetProperty("name").GetString() } });

        foreach (var f in Tone.Frames(500, true).Concat(Tone.Frames(800, false)))
            await Send(ws, new { @event = "media", streamSid = id, media = new { track = "inbound", payload = Convert.ToBase64String(f) } });

        await Wait.Until(() => got.Count(m => Ev(m) == "transcript") >= 3);
        var transcript = got.Where(m => Ev(m) == "transcript").ToList();
        Assert.Equal("user", transcript[1].GetProperty("role").GetString());
        Assert.Equal("എനിക്ക് ഒരു വെബ്സൈറ്റ് വേണം", transcript[1].GetProperty("text").GetString());
        Assert.Equal("ml", transcript[1].GetProperty("language").GetString());
        Assert.Equal("ശരി! എന്ത് തരം വെബ്സൈറ്റ്?", transcript[2].GetProperty("text").GetString());

        await Send(ws, new { @event = "stop", streamSid = id });
        await Wait.Until(() =>
        {
            using var scope = app.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<HarnessDbContext>().Outbox.Any(o => o.EventType == "call.completed" && o.TenantId == tenantId);
        });
    }

    [Fact]
    public async Task A_talk_pass_works_once_and_never_on_the_phone_endpoint()
    {
        using var app = App(new FakeTts(), new FakeModel("Hi."));
        var (c, _) = await app.NewTenantAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().PostAsync("/v1/voice/web/ticket", null)).StatusCode);
        var t = JsonDocument.Parse(await (await c.PostAsync("/v1/voice/web/ticket", null)).Content.ReadAsStringAsync()).RootElement;
        var (id, token) = (t.GetProperty("id").GetString()!, t.GetProperty("token").GetString()!);

        async Task<WebSocketCloseStatus?> TryStart(string path)
        {
            var ws = await app.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost" + path), CancellationToken.None);
            await Send(ws, new { @event = "start", streamSid = id, start = new { streamSid = id, callSid = id, customParameters = new { token } } });
            var buf = new byte[1 << 16];
            var r = await ws.ReceiveAsync(buf, CancellationToken.None);
            return r.MessageType == WebSocketMessageType.Close ? ws.CloseStatus : null;
        }

        // The phone endpoint is off here (voice not enabled) and refuses the upgrade outright.
        var phone = await Assert.ThrowsAnyAsync<Exception>(() => TryStart("/voice/twilio/stream"));
        Assert.Null(await TryStart("/voice/web/stream"));                                     // first use: accepted (greeting arrives)
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await TryStart("/voice/web/stream")); // replay: refused
    }
}
