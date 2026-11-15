using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgencyHarness.Infrastructure;

namespace AgencyHarness.Voice;

public class TwilioOptions
{
    public string? AccountSid { get; set; }
    /// <summary>Used to check that webhooks really come from Twilio, and for call control.</summary>
    public string? AuthToken { get; set; }
    /// <summary>Reject unsigned webhooks. Only turn off for local testing.</summary>
    public bool ValidateSignature { get; set; } = true;
    public string ApiBaseUrl { get; set; } = "https://api.twilio.com";
}

public class VoiceOptions
{
    public bool Enabled { get; set; }
    /// <summary>The agency (tenant) that owns this phone line; call records and events go to it.</summary>
    public Guid? TenantId { get; set; }
    /// <summary>The public https address Twilio reaches, e.g. your Cloudflare tunnel. Needed for signatures and the stream URL.</summary>
    public string? PublicBaseUrl { get; set; }
    /// <summary>Signs the one-time stream token. Defaults to a random value per process.</summary>
    public string? StreamSecret { get; set; }
    public string DefaultLanguage { get; set; } = "ml";
    /// <summary>How confident language ID must be to switch on a single utterance (otherwise two in a row).</summary>
    public double LanguageSwitchScore { get; set; } = 0.8;
    public string? TransferNumber { get; set; }
    /// <summary>Distress intensity (0–1) that triggers a transfer.</summary>
    public double EscalateDistress { get; set; } = 0.6;
    public string Persona { get; set; } = "You are a friendly, efficient AI receptionist for a digital agency. You are an AI and say so if asked. " +
        "Find out who is calling, their callback number and why. Take clear messages. Never share the owner's personal details or schedule, " +
        "and don't make commitments, prices or appointments on the owner's behalf.";
    public Dictionary<string, string> Greetings { get; set; } = new()
    {
        ["ml"] = "നമസ്കാരം, ഞാൻ ഒരു AI അസിസ്റ്റന്റ് ആണ്. എങ്ങനെ സഹായിക്കാം? Hello, I'm an AI assistant. How can I help?",
        ["*"] = "Hello, you've reached the receptionist line. I'm an AI assistant. How can I help?",
    };
    public Dictionary<string, string> TransferLines { get; set; } = new()
    {
        ["ml"] = "ഒരു നിമിഷം, ഞാൻ നിങ്ങളെ ഇപ്പോൾ കണക്ട് ചെയ്യാം.",
        ["fr"] = "Un instant, je vous mets en relation tout de suite.",
        ["*"] = "One moment, I'm connecting you now.",
    };
    public Dictionary<string, string> FallbackLines { get; set; } = new()
    {
        ["ml"] = "ക്ഷമിക്കണം, ഇപ്പോൾ ഒരു പ്രശ്നമുണ്ട്. ദയവായി നിങ്ങളുടെ പേരും നമ്പറും പറയൂ.",
        ["*"] = "Sorry, I'm having trouble right now. Please tell me your name and number and we'll call you back.",
    };
    public VadOptions Vad { get; set; } = new();
    public TwilioOptions Twilio { get; set; } = new();

    // Providers (see docs/VOICE-ENGINE.md for what to point these at).
    public OpenAiCompatibleOptions? CustomLlm { get; set; }
    public bool ClaudeFallback { get; set; } = true;
    public string ClaudeModel { get; set; } = "claude-opus-5-5";
    public int FirstTokenTimeoutMs { get; set; } = 2500;
    public HttpSttOptions? Stt { get; set; }
    public IndicLidOptions? LanguageId { get; set; }
    public OpenAiSpeechOptions? LocalTts { get; set; }
    public GeminiTtsOptions? GeminiTts { get; set; }
    /// <summary>TTS chain per language: names are "local" and "gemini". "*" is the default.</summary>
    public Dictionary<string, string[]> TtsRouting { get; set; } = new() { ["ml"] = ["gemini"], ["*"] = ["local", "gemini"] };
}

/// <summary>Twilio request signature: base64(HMAC-SHA1(authToken, url + sorted(name+value)...)).</summary>
public static class TwilioSignature
{
    public static string Compute(string authToken, string url, IEnumerable<KeyValuePair<string, string>> form)
    {
        var sb = new StringBuilder(url);
        foreach (var kv in form.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append(kv.Value);
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(authToken));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public static bool Verify(string authToken, string url, IEnumerable<KeyValuePair<string, string>> form, string? signature) =>
        !string.IsNullOrEmpty(signature) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Compute(authToken, url, form)), Encoding.UTF8.GetBytes(signature));
}

/// <summary>Short-lived HMAC token that ties a media stream to the call Twilio told us about.</summary>
public class StreamTokens(VoiceOptions o)
{
    private readonly byte[] _secret = string.IsNullOrEmpty(o.StreamSecret) ? RandomNumberGenerator.GetBytes(32) : Encoding.UTF8.GetBytes(o.StreamSecret);

    public string Issue(string callSid, DateTimeOffset now)
    {
        var exp = now.AddMinutes(2).ToUnixTimeSeconds();
        return $"{exp}.{Sign($"{callSid}|{exp}")}";
    }

    public bool Validate(string? token, string callSid, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token) || token.Split('.') is not [var expStr, var sig] || !long.TryParse(expStr, out var exp)) return false;
        if (now.ToUnixTimeSeconds() > exp) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign($"{callSid}|{exp}")), Encoding.UTF8.GetBytes(sig));
    }

    private string Sign(string s)
    {
        using var h = new HMACSHA256(_secret);
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    }
}

/// <summary>Moves a live call to a human: replaces the call's instructions with a &lt;Dial&gt;.</summary>
public class TwilioCallControl(HttpClient http, TwilioOptions o) : ICallControl
{
    public async Task TransferAsync(string callSid, string toNumber, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(o.AccountSid) || string.IsNullOrEmpty(o.AuthToken)) throw new InvalidOperationException("Twilio credentials not set.");
        var twiml = $"<Response><Dial>{System.Security.SecurityElement.Escape(toNumber)}</Dial></Response>";
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.ApiBaseUrl.TrimEnd('/')}/2010-04-01/Accounts/{o.AccountSid}/Calls/{callSid}.json")
        {
            Content = new FormUrlEncodedContent([new("Twiml", twiml)]),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{o.AccountSid}:{o.AuthToken}")));
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }
}

/// <summary>Writes Twilio Media Stream messages back over the WebSocket (one writer at a time).</summary>
public class TwilioSink(WebSocket ws, string streamSid) : IVoiceSink
{
    private readonly SemaphoreSlim _send = new(1, 1);

    public Task SendAudioAsync(byte[] mulaw20ms, CancellationToken ct) =>
        SendAsync(new { @event = "media", streamSid, media = new { payload = Convert.ToBase64String(mulaw20ms) } }, ct);
    public Task ClearAsync(CancellationToken ct) => SendAsync(new { @event = "clear", streamSid }, ct);
    public Task MarkAsync(string name, CancellationToken ct) => SendAsync(new { @event = "mark", streamSid, mark = new { name } }, ct);

    private async Task SendAsync(object msg, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg);
        await _send.WaitAsync(ct);
        try { if (ws.State == WebSocketState.Open) await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _send.Release(); }
    }
}

public static class TwilioVoiceEndpoints
{
    public static void MapTwilioVoice(this WebApplication app)
    {
        // 1. Twilio calls this when a call comes in. We answer with TwiML that opens a two-way media stream.
        app.MapPost("/voice/twilio/incoming", async (HttpContext http, VoiceOptions o, StreamTokens tokens, TimeProvider clock) =>
        {
            if (!o.Enabled) return Results.NotFound();
            var form = await http.Request.ReadFormAsync();
            if (o.Twilio.ValidateSignature)
            {
                var url = (o.PublicBaseUrl ?? $"{http.Request.Scheme}://{http.Request.Host}").TrimEnd('/') + http.Request.Path + http.Request.QueryString;
                if (string.IsNullOrEmpty(o.Twilio.AuthToken) ||
                    !TwilioSignature.Verify(o.Twilio.AuthToken, url, form.Select(f => new KeyValuePair<string, string>(f.Key, f.Value.ToString())),
                        http.Request.Headers["X-Twilio-Signature"].FirstOrDefault()))
                    return Results.StatusCode(403);
            }
            var callSid = form["CallSid"].ToString();
            if (string.IsNullOrEmpty(callSid)) return Results.BadRequest();

            var baseUrl = (o.PublicBaseUrl ?? $"{http.Request.Scheme}://{http.Request.Host}").TrimEnd('/');
            var wsUrl = baseUrl.Replace("https://", "wss://").Replace("http://", "ws://") + "/voice/twilio/stream";
            var token = tokens.Issue(callSid, clock.GetUtcNow());
            var twiml = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <Response><Connect><Stream url="{System.Security.SecurityElement.Escape(wsUrl)}"><Parameter name="token" value="{token}"/></Stream></Connect></Response>
                """;
            return Results.Content(twiml, "text/xml");
        });

        // 2. Twilio connects here and streams the call audio both ways.
        app.Map("/voice/twilio/stream", async (HttpContext http, VoiceOptions o, StreamTokens tokens, VoiceProviders providers,
            TimeProvider clock, ILoggerFactory lf, IServiceScopeFactory scopes, Outbox outbox) =>
        {
            if (!o.Enabled || !http.WebSockets.IsWebSocketRequest) { http.Response.StatusCode = 400; return; }
            var log = lf.CreateLogger("Voice");
            using var ws = await http.WebSockets.AcceptWebSocketAsync();
            CallSession? session = null;
            string callSid = "", streamSid = "";
            var started = clock.GetUtcNow();
            var buffer = new byte[64 * 1024];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var msg = await ReceiveTextAsync(ws, buffer, http.RequestAborted);
                    if (msg is null) break;
                    using var doc = JsonDocument.Parse(msg);
                    var root = doc.RootElement;
                    switch (root.GetProperty("event").GetString())
                    {
                        case "start":
                            var start = root.GetProperty("start");
                            streamSid = start.GetProperty("streamSid").GetString()!;
                            callSid = start.GetProperty("callSid").GetString()!;
                            var token = start.TryGetProperty("customParameters", out var cp) && cp.TryGetProperty("token", out var t) ? t.GetString() : null;
                            if (!tokens.Validate(token, callSid, clock.GetUtcNow()))
                            {
                                log.LogWarning("Rejected media stream for {Call}: bad or expired token", callSid);
                                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", CancellationToken.None);
                                return;
                            }
                            session = new CallSession(callSid, new TwilioSink(ws, streamSid), providers, o, log);
                            log.LogInformation("Call {Call} connected", callSid);
                            await session.StartAsync();
                            break;
                        case "media" when session is not null:
                            var media = root.GetProperty("media");
                            if (!media.TryGetProperty("track", out var track) || track.GetString() == "inbound")
                                session.OnAudio(Convert.FromBase64String(media.GetProperty("payload").GetString()!));
                            break;
                        case "mark" when session is not null:
                            session.OnMark(root.GetProperty("mark").GetProperty("name").GetString()!);
                            break;
                        case "stop":
                            goto done;
                    }
                }
            done:;
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
            finally
            {
                if (session is not null)
                {
                    await session.DisposeAsync();
                    await RecordCallAsync(scopes, outbox, o, callSid, started, clock.GetUtcNow(), session);
                }
            }
        });
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket ws, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }
    }

    /// <summary>After the call: one "call.completed" event (webhook + event log) with the transcript and turn metrics.</summary>
    private static async Task RecordCallAsync(IServiceScopeFactory scopes, Outbox outbox, VoiceOptions o, string callSid,
        DateTimeOffset started, DateTimeOffset ended, CallSession session)
    {
        if (o.TenantId is not { } tenantId) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarnessDbContext>();
        var turns = session.Turns;
        outbox.Add(db, tenantId, "call.completed", new
        {
            callSid, durationSeconds = (int)(ended - started).TotalSeconds, finalLanguage = session.Language, transferred = session.Transferred,
            languages = turns.Select(t => t.Language).Distinct(),
            turns = turns.Select(t => new { t.Index, t.Language, caller = t.UserText, emotion = t.Emotion, reply = t.AssistantText, t.Interrupted, t.TimeToFirstAudioMs, t.Model }),
        });
        await db.SaveChangesAsync();
    }
}
