using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using AgencyHarness.Infrastructure;

namespace AgencyHarness.Voice;

/// <summary>Where a call's audio goes back to (a Twilio media stream, a WebRTC peer, a test).</summary>
public interface IVoiceSink
{
    Task SendAudioAsync(byte[] mulaw20ms, CancellationToken ct);
    /// <summary>Drop any audio the carrier has buffered but not played yet (barge-in).</summary>
    Task ClearAsync(CancellationToken ct);
    /// <summary>Ask the carrier to tell us when playback reaches this point.</summary>
    Task MarkAsync(string name, CancellationToken ct);
}

/// <summary>Optional: sinks that can show a live transcript (the browser does; Twilio doesn't).</summary>
public interface ITranscriptSink
{
    Task TranscriptAsync(string role, string text, string language, string? emotion, bool interrupted, CancellationToken ct);
}

public interface ICallControl
{
    Task TransferAsync(string callId, string toNumber, CancellationToken ct);
}

/// <summary>All the pluggable pieces one call uses.</summary>
public record VoiceProviders(ISpeechToText Stt, ITextToSpeech Tts, IResponseModel Model, IEmotionDetector Emotion,
    ILanguageId? LanguageId, ICallControl? CallControl);

public record VoiceTurn(int Index, string Language, string? UserText, string? Emotion, string? AssistantText,
    bool Interrupted, double? TimeToFirstAudioMs, string? Model);

public static class VoiceMetrics
{
    private static readonly Meter Meter = new(Telemetry.MeterName);
    public static readonly Histogram<double> TimeToFirstAudio = Meter.CreateHistogram<double>("voice.time_to_first_audio", "ms");
    public static readonly Histogram<double> BargeInStop = Meter.CreateHistogram<double>("voice.barge_in_stop", "ms");
    public static readonly Counter<long> BargeIns = Meter.CreateCounter<long>("voice.barge_ins");
    public static readonly Counter<long> LanguageSwitches = Meter.CreateCounter<long>("voice.language_switches");
    public static readonly Counter<long> Escalations = Meter.CreateCounter<long>("voice.escalations");
}

/// <summary>
/// One live call. Audio arrives in 20 ms frames; voice activity detection finds the caller's turns;
/// each turn runs speech-to-text → language + emotion → response model (streamed) → text-to-speech,
/// sentence by sentence so speaking starts early. Everything in a turn shares one cancellation token,
/// so when the caller talks over us (barge-in) playback is cleared and generation stops at once.
/// </summary>
public sealed class CallSession : IAsyncDisposable
{
    private readonly string _callId;
    private readonly IVoiceSink _sink;
    private readonly VoiceProviders _p;
    private readonly VoiceOptions _o;
    private readonly ILogger _log;
    private readonly EnergyVad _vad;
    private readonly LanguageTracker _lang;
    private readonly object _gate = new();
    private readonly List<ChatTurn> _history = new();
    private readonly List<VoiceTurn> _turns = new();
    private readonly List<EmotionResult> _emotions = new();
    private readonly Dictionary<string, (int Turn, string Text)> _pendingMarks = new();
    private readonly Dictionary<int, StringBuilder> _heard = new();

    private CancellationTokenSource? _turnCts;
    private Task _turnTask = Task.CompletedTask;
    private int _turnIndex;
    private int _markSeq;
    private bool _generating;
    private PcmAudio? _unprocessed;      // audio of a turn cancelled before it was transcribed
    private bool _transferred;

    public CallSession(string callId, IVoiceSink sink, VoiceProviders providers, VoiceOptions options, ILogger log)
    {
        _callId = callId; _sink = sink; _p = providers; _o = options; _log = log;
        _vad = new EnergyVad(options.Vad);
        _lang = new LanguageTracker(options.DefaultLanguage, options.LanguageSwitchScore);
    }

    public string Language => _lang.Current;
    public IReadOnlyList<VoiceTurn> Turns { get { lock (_gate) return _turns.ToList(); } }
    public bool IsSpeaking { get { lock (_gate) return _generating || _pendingMarks.Count > 0; } }
    public bool Transferred => _transferred;

    /// <summary>Greet the caller. Spoken as turn 0 with no user input.</summary>
    public Task StartAsync()
    {
        var greeting = _o.Greetings.TryGetValue(_lang.Current, out var g) ? g : _o.Greetings.GetValueOrDefault("*", "Hello, how can I help?");
        StartTurn(ct => SpeakScriptedAsync(greeting, ct));
        return Task.CompletedTask;
    }

    /// <summary>Feed one inbound 20 ms μ-law frame. Called in order by the channel's reader.</summary>
    public void OnAudio(byte[] mulaw)
    {
        if (_transferred) return;
        var ev = _vad.Process(G711.DecodeAll(mulaw), out var utterance);
        if (ev == VadEvent.SpeechStart && IsSpeaking) BargeIn();
        else if (ev == VadEvent.SpeechEnd && utterance is not null)
        {
            if (_unprocessed is { } prev)   // the caller kept talking before we'd understood the last bit
            {
                utterance = new PcmAudio(prev.Samples.Concat(utterance.Samples).ToArray(), utterance.SampleRate);
                _unprocessed = null;
            }
            var u = utterance;
            StartTurn(ct => RespondAsync(u, ct));
        }
    }

    /// <summary>The carrier played audio up to this mark: those words were heard.</summary>
    public void OnMark(string name)
    {
        lock (_gate)
        {
            if (!_pendingMarks.Remove(name, out var m)) return;
            if (!_heard.TryGetValue(m.Turn, out var sb)) _heard[m.Turn] = sb = new StringBuilder();
            sb.Append(sb.Length > 0 ? " " : "").Append(m.Text);
        }
    }

    private void BargeIn()
    {
        var sw = Stopwatch.StartNew();
        int turn;
        lock (_gate)
        {
            turn = _turnIndex;
            _turnCts?.Cancel();
            _pendingMarks.Clear();
            _generating = false;
        }
        _ = _sink.ClearAsync(CancellationToken.None);
        VoiceMetrics.BargeIns.Add(1);
        VoiceMetrics.BargeInStop.Record(sw.Elapsed.TotalMilliseconds);
        _log.LogInformation("Call {Call}: caller interrupted turn {Turn}", _callId, turn);

        // Keep only what the caller actually heard in the conversation history.
        lock (_gate)
        {
            var heard = _heard.TryGetValue(turn, out var sb) ? sb.ToString() : "";
            var idx = _history.FindLastIndex(h => h.Role == "assistant");
            if (idx >= 0 && _turns.Count > 0 && _turns[^1].Index == turn)
                _history[idx] = new ChatTurn("assistant", heard.Length > 0 ? heard + " …(interrupted)" : "…(interrupted)");
            if (_turns.Count > 0 && _turns[^1].Index == turn)
                _turns[^1] = _turns[^1] with { Interrupted = true, AssistantText = heard };
        }
    }

    private void StartTurn(Func<CancellationToken, Task> work)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _turnCts?.Cancel();
            _turnCts = cts = new CancellationTokenSource();
            _turnIndex++;
            _generating = true;
        }
        var previous = _turnTask;
        _turnTask = Task.Run(async () =>
        {
            try { await previous; } catch { /* the previous turn's outcome doesn't matter now */ }
            try { await work(cts.Token); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception e) { _log.LogError(e, "Call {Call}: turn failed", _callId); }
            finally { lock (_gate) if (_turnCts == cts) _generating = false; }
        });
    }

    private async Task SpeakScriptedAsync(string text, CancellationToken ct)
    {
        int turn; lock (_gate) turn = _turnIndex;
        await SpeakSentenceAsync(turn, text, style: null, Stopwatch.StartNew(), ct);
        lock (_gate)
        {
            _history.Add(new ChatTurn("assistant", text));
            _turns.Add(new VoiceTurn(turn, _lang.Current, null, null, text, false, null, "scripted"));
        }
        await ShowAsync("assistant", text, null, false);
    }

    private async Task RespondAsync(PcmAudio utterance, CancellationToken ct)
    {
        var sinceEndOfSpeech = Stopwatch.StartNew();
        int turn; lock (_gate) turn = _turnIndex;
        _unprocessed = utterance;

        // 1. Which language is the caller speaking now?
        if (_p.LanguageId is not null)
        {
            try
            {
                if (_lang.Observe(await _p.LanguageId.DetectAsync(utterance, ct)))
                {
                    VoiceMetrics.LanguageSwitches.Add(1);
                    _log.LogInformation("Call {Call}: language switched to {Lang}", _callId, _lang.Current);
                }
            }
            catch (ProviderUnavailableException e) { _log.LogWarning("Language ID unavailable: {Err}", e.Message); }
        }

        // 2. What did they say?
        var stt = await _p.Stt.TranscribeAsync(utterance, _lang.Current, ct);
        _unprocessed = null;
        if (string.IsNullOrWhiteSpace(stt.Text)) return;
        // A speech-to-text engine that detects language itself can also move the call's language.
        if (_p.LanguageId is null && stt.Language is { } sttLang && _lang.Observe(new LangResult(sttLang, stt.LanguageConfidence ?? 1)))
            VoiceMetrics.LanguageSwitches.Add(1);

        // 3. How do they feel?
        var emotion = await _p.Emotion.DetectAsync(utterance, stt.Text, _lang.Current, ct);
        List<ChatTurn> history;
        lock (_gate)
        {
            _emotions.Add(emotion);
            _history.Add(new ChatTurn("user", stt.Text));
            history = _history.ToList();
        }

        await ShowAsync("user", stt.Text, emotion.Label, false);

        // 4. Rules before the model: distress, or anger that keeps rising, goes to a human.
        if (ShouldEscalate() && _p.CallControl is not null && !string.IsNullOrEmpty(_o.TransferNumber))
        {
            var line = _o.TransferLines.TryGetValue(_lang.Current, out var l) ? l : _o.TransferLines.GetValueOrDefault("*", "Let me connect you now.");
            await SpeakSentenceAsync(turn, line, StyleFor(emotion), sinceEndOfSpeech, ct);
            lock (_gate) _turns.Add(new VoiceTurn(turn, _lang.Current, stt.Text, emotion.Label, line, false, null, "policy:transfer"));
            await WaitForPlaybackAsync(TimeSpan.FromSeconds(6), ct);
            VoiceMetrics.Escalations.Add(1);
            _transferred = true;
            await _p.CallControl.TransferAsync(_callId, _o.TransferNumber!, ct);
            return;
        }

        // 5. Stream the reply; speak each sentence as soon as it's complete.
        var prompt = BuildSystemPrompt(emotion);
        var chunker = new SentenceChunker();
        var said = new StringBuilder();
        double? ttfa = null;
        lock (_gate)
        {
            _history.Add(new ChatTurn("assistant", ""));
            _turns.Add(new VoiceTurn(turn, _lang.Current, stt.Text, emotion.Label, "", false, null, null));
        }
        try
        {
            await foreach (var token in _p.Model.RespondAsync(prompt, history, ct))
                foreach (var sentence in chunker.Push(token))
                {
                    var t = await SpeakSentenceAsync(turn, sentence, StyleFor(emotion), sinceEndOfSpeech, ct);
                    ttfa ??= t;
                    said.Append(said.Length > 0 ? " " : "").Append(sentence);
                }
            if (chunker.Flush() is { } rest)
            {
                var t = await SpeakSentenceAsync(turn, rest, StyleFor(emotion), sinceEndOfSpeech, ct);
                ttfa ??= t;
                said.Append(said.Length > 0 ? " " : "").Append(rest);
            }
        }
        catch (ProviderUnavailableException e)
        {
            _log.LogError("Call {Call}: no response model: {Err}", _callId, e.Message);
            var sorry = _o.FallbackLines.TryGetValue(_lang.Current, out var f) ? f : _o.FallbackLines.GetValueOrDefault("*", "Sorry, I'm having trouble. Please leave your name and number.");
            await SpeakSentenceAsync(turn, sorry, null, sinceEndOfSpeech, ct);
            said.Append(sorry);
        }
        finally
        {
            lock (_gate)
            {
                var idx = _turns.FindLastIndex(t => t.Index == turn);
                if (idx >= 0 && _turns[idx].Interrupted) _ = ShowAsync("assistant", _turns[idx].AssistantText ?? "", null, true);
                if (idx >= 0 && !_turns[idx].Interrupted)
                {
                    _ = ShowAsync("assistant", said.ToString(), null, false);
                    _turns[idx] = _turns[idx] with { AssistantText = said.ToString(), TimeToFirstAudioMs = ttfa, Model = (_p.Model as FallbackResponseModel)?.LastUsed ?? _p.Model.Name };
                    var h = _history.FindLastIndex(x => x.Role == "assistant");
                    if (h >= 0) _history[h] = new ChatTurn("assistant", said.ToString());
                }
            }
        }
    }

    /// <summary>Synthesize, send, and mark one sentence. Returns time-to-first-audio when this was the turn's first audio.</summary>
    private async Task<double?> SpeakSentenceAsync(int turn, string sentence, string? style, Stopwatch sinceEndOfSpeech, CancellationToken ct)
    {
        var audio = await _p.Tts.SpeakAsync(sentence, _lang.Current, style, ct);
        ct.ThrowIfCancellationRequested();
        var pcm8k = Pcm.Resample(audio.Samples, audio.SampleRate, 8000);
        bool first;
        lock (_gate) first = !_heard.ContainsKey(turn) && !_pendingMarks.Values.Any(m => m.Turn == turn);
        double? ttfa = null;
        foreach (var frame in Pcm.ToMulawFrames(pcm8k))
        {
            ct.ThrowIfCancellationRequested();
            await _sink.SendAudioAsync(frame, ct);
            if (ttfa is null && first)
            {
                ttfa = sinceEndOfSpeech.Elapsed.TotalMilliseconds;
                VoiceMetrics.TimeToFirstAudio.Record(ttfa.Value);
            }
        }
        var mark = $"t{turn}-{Interlocked.Increment(ref _markSeq)}";
        lock (_gate) _pendingMarks[mark] = (turn, sentence);
        await _sink.MarkAsync(mark, ct);
        return ttfa;
    }

    private Task ShowAsync(string role, string text, string? emotion, bool interrupted) =>
        _sink is ITranscriptSink t && text.Length > 0
            ? t.TranscriptAsync(role, text, _lang.Current, emotion, interrupted, CancellationToken.None)
            : Task.CompletedTask;

    private async Task WaitForPlaybackAsync(TimeSpan max, CancellationToken ct)
    {
        var until = DateTime.UtcNow + max;
        while (DateTime.UtcNow < until)
        {
            lock (_gate) if (_pendingMarks.Count == 0) return;
            await Task.Delay(50, ct);
        }
    }

    private bool ShouldEscalate()
    {
        lock (_gate)
        {
            if (_emotions.Count == 0) return false;
            var last = _emotions[^1];
            if (last.Label == "distressed" && last.Intensity >= _o.EscalateDistress) return true;
            // Anger in two turns in a row, the second at least as strong.
            return _emotions.Count >= 2 && _emotions[^1].Label == "angry" && _emotions[^2].Label == "angry"
                   && _emotions[^1].Intensity >= _emotions[^2].Intensity;
        }
    }

    private static string? StyleFor(EmotionResult e) => e.Label switch
    {
        "angry" => "Speak calmly, slowly and apologetically",
        "distressed" => "Speak gently, calmly and reassuringly",
        "confused" => "Speak slowly and clearly",
        "happy" => "Speak warmly and cheerfully",
        _ => null,
    };

    private string BuildSystemPrompt(EmotionResult emotion)
    {
        var lang = LanguageNames.Get(_lang.Current);
        var mood = emotion.Label switch
        {
            "angry" => "The caller sounds frustrated or angry: acknowledge it briefly, don't argue, and offer a concrete next step.",
            "distressed" => "The caller sounds distressed: be gentle, calm and brief, and offer to connect them to a person.",
            "confused" => "The caller sounds confused: use very simple words and one idea at a time.",
            "happy" => "The caller sounds happy: match their warmth.",
            _ => "",
        };
        return $"""
            Your name is {_o.AgentName}. Introduce yourself by that name.
            {_o.Persona}

            This is a live phone call. Reply ONLY in {lang} (the caller's current language), in natural spoken style:
            one or two short sentences, no lists, no markdown, no emojis. Read numbers the way people say them.
            If the caller switches language, the next reply will be in their new language.
            {mood}
            """;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _turnCts?.Cancel();
        try { await _turnTask; } catch { }
    }
}

/// <summary>Splits streamed tokens into speakable sentences (Latin, Malayalam and Devanagari punctuation).</summary>
public class SentenceChunker
{
    private readonly StringBuilder _buf = new();
    private static readonly char[] Enders = ['.', '!', '?', '।', '॥', '\n'];

    public IEnumerable<string> Push(string token)
    {
        _buf.Append(token);
        while (true)
        {
            var s = _buf.ToString();
            int cut = -1;
            for (int i = 0; i < s.Length - 1; i++)
                if (Enders.Contains(s[i]) && char.IsWhiteSpace(s[i + 1]) && i >= 1) { cut = i + 1; break; }
            // Very long run without punctuation: break at a comma so speech can start.
            if (cut < 0 && s.Length > 160) { var c = s.LastIndexOf(',', 160); if (c > 40) cut = c + 1; }
            if (cut < 0) yield break;
            var sentence = s[..cut].Trim();
            _buf.Remove(0, cut);
            if (sentence.Length > 0) yield return sentence;
        }
    }

    public string? Flush()
    {
        var rest = _buf.ToString().Trim();
        _buf.Clear();
        return rest.Length > 0 ? rest : null;
    }
}

public static class LanguageNames
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["ml"] = "Malayalam", ["en"] = "English", ["fr"] = "French", ["hi"] = "Hindi", ["ta"] = "Tamil",
        ["te"] = "Telugu", ["kn"] = "Kannada", ["pa"] = "Punjabi", ["es"] = "Spanish", ["ar"] = "Arabic", ["zh"] = "Chinese",
    };
    public static string Get(string code) => Names.GetValueOrDefault(code, code);
}
