namespace AgencyHarness.Voice;

public enum VadEvent { None, SpeechStart, SpeechEnd }

public class VadOptions
{
    /// <summary>How long speech must last before it counts (filters coughs and clicks).</summary>
    public int StartMs { get; set; } = 200;
    /// <summary>How much silence ends the caller's turn.</summary>
    public int EndSilenceMs { get; set; } = 600;
    /// <summary>Audio kept from just before speech was confirmed, so the first syllable isn't lost.</summary>
    public int PreRollMs { get; set; } = 300;
    /// <summary>Speech must be this many dB above the running noise floor.</summary>
    public double MarginDb { get; set; } = 12;
    /// <summary>...and never quieter than this, whatever the noise floor.</summary>
    public double MinSpeechDb { get; set; } = -42;
    /// <summary>Longest single utterance before it's cut and sent anyway.</summary>
    public int MaxUtteranceMs { get; set; } = 15_000;
}

/// <summary>
/// Energy-based voice activity detector with an adaptive noise floor. Works on 20 ms frames.
/// It is the default, dependency-free detector; a model-based one (Silero) can replace it behind
/// the same calls. Runs continuously, including while we're speaking, which is what makes barge-in
/// possible.
/// </summary>
public sealed class EnergyVad(VadOptions options, int sampleRate = 8000)
{
    private const int FrameMs = 20;
    private double _noiseDb = -60;
    private bool _inSpeech;
    private int _speechMs, _silenceMs;
    private readonly List<short> _utterance = new();
    private readonly Queue<short[]> _preRoll = new();

    public bool InSpeech => _inSpeech;
    public double NoiseFloorDb => _noiseDb;

    /// <summary>Feed one 20 ms frame. On SpeechEnd, <paramref name="utterance"/> holds the audio.</summary>
    public VadEvent Process(short[] frame, out PcmAudio? utterance)
    {
        utterance = null;
        double level = Pcm.LevelDb(frame);
        bool loud = level > Math.Max(_noiseDb + options.MarginDb, options.MinSpeechDb);

        if (!_inSpeech)
        {
            // Track the noise floor only while nobody is talking (fast down, slow up).
            if (!loud) _noiseDb = level < _noiseDb ? level : _noiseDb * 0.95 + level * 0.05;
            _preRoll.Enqueue(frame);
            while (_preRoll.Count * FrameMs > options.PreRollMs + options.StartMs) _preRoll.Dequeue();

            _speechMs = loud ? _speechMs + FrameMs : 0;
            if (_speechMs >= options.StartMs)
            {
                _inSpeech = true;
                _silenceMs = 0;
                _utterance.Clear();
                foreach (var f in _preRoll) _utterance.AddRange(f);
                _preRoll.Clear();
                return VadEvent.SpeechStart;
            }
            return VadEvent.None;
        }

        _utterance.AddRange(frame);
        _silenceMs = loud ? 0 : _silenceMs + FrameMs;
        bool tooLong = _utterance.Count * 1000L / sampleRate >= options.MaxUtteranceMs;
        if (_silenceMs >= options.EndSilenceMs || tooLong)
        {
            _inSpeech = false;
            _speechMs = 0;
            // Trim the trailing silence we waited through.
            int trim = Math.Min(_utterance.Count, _silenceMs * sampleRate / 1000);
            utterance = new PcmAudio(_utterance.GetRange(0, _utterance.Count - trim).ToArray(), sampleRate);
            _utterance.Clear();
            return VadEvent.SpeechEnd;
        }
        return VadEvent.None;
    }
}
