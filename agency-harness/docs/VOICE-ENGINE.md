# Voice engine: your models, any channel

The harness is the product. The phone receptionist is one **channel**, one way in. Other channels
are web chat, WhatsApp, the dashboard and MCP. Every channel feeds the same harness core, and live
voice gets a real-time engine where **your own models** do the listening, deciding and speaking:

- custom-trained models for the languages you choose, Malayalam first
- quick handling when a caller interrupts (barge-in)
- following the caller when they switch language mid-call
- picking up emotional changes and adapting tone, or handing the call to a human

## Big picture

```mermaid
flowchart LR
  subgraph Channels["Channels (thin adapters)"]
    PH[Phone<br/>Twilio Media Streams / SIP]
    WEB[Web voice<br/>WebRTC]
    WA[WhatsApp / SMS]
    MCP[MCP + REST]
  end
  subgraph Harness
    VE[Voice engine<br/>real-time, per call]
    CORE[Harness core<br/>tenants · conversations · policies]
    Q[(Job queue<br/>async work)]
  end
  subgraph Models["Model providers (pluggable)"]
    STT[Speech-to-text]
    LID[Language ID]
    SER[Emotion]
    LLM[Response model<br/>your custom model / Claude]
    TTS[Text-to-speech]
  end
  GPU[Your GPU box<br/>inference service]
  PH -- "audio stream (WebSocket)" --> VE
  WEB --> VE
  WA --> CORE
  MCP --> CORE
  VE <--> CORE
  VE <-- "streaming gRPC / WebSocket" --> Models
  Models --- GPU
  CORE --> Q
```

The harness owns the call, its state, its rules and its records. Channels only move audio or text,
so changing phone carriers or adding WhatsApp never touches the engine.

## Two paths, two time budgets (SD101: sync vs async, latency)

| Path | Budget | What runs there |
|---|---|---|
| **Real-time**, during the call | Start speaking within **~800 ms** of the caller stopping; stop within **~200 ms** when interrupted | Voice activity detection, streaming speech-to-text, language ID, emotion, response model (streamed), streaming text-to-speech |
| **Async**, after or beside the call | Seconds to minutes | Call summary, CRM and ledger writes, follow-up jobs, texting you, quality review, collecting training data |

Nothing slow is allowed on the real-time path. Anything that can wait goes on the job queue the
harness already has, with retries and a dead-letter queue.

## One turn of a live call

```mermaid
sequenceDiagram
  participant C as Caller
  participant G as Voice engine
  participant S as STT + language ID + emotion
  participant M as Response model
  participant T as TTS
  C->>G: audio frames (20 ms)
  G->>S: stream frames
  S-->>G: partial text, language=ml, emotion=calm
  Note over G: VAD: caller stopped speaking<br/>(or turn detector is confident)
  G->>M: context + final text + {lang, emotion, style}
  M-->>G: tokens (streamed)
  G->>T: sentence 1 as soon as it's complete
  T-->>C: audio (streamed)
  C->>G: caller starts talking again (barge-in)
  G--xT: cancel playback (<200 ms)
  G--xM: cancel generation
  Note over G: keep only what the caller actually heard
```

### Barge-in (interruptions)
- Voice activity detection keeps running **while we're speaking**. As soon as it sees real speech (not a cough, and longer than about 250 ms), the engine:
  1. stops sending audio to the line. The carrier's "clear" message empties its buffer, so the caller stops hearing us within about 200 ms.
  2. cancels the running TTS and response-model calls through one cancellation token per turn.
  3. records only the part of our answer that was actually played, so the model knows what the caller heard.
- A small turn-detection model (for example Smart Turn or LiveKit's turn detector) decides when the caller has *finished*, rather than relying only on a silence timer. That cuts both awkward pauses and talking over people.

### Language changes
- Language ID runs on **every utterance**, not just the first. IndicConformer's `/lid` endpoint already does this on your PC.
- The engine keeps one **current language** per call. When language ID is confident about a new one for a full utterance, it switches. Code-mixed speech (Malayalam with English words) stays in the caller's main language.
- Speech-to-text, the response model and text-to-speech are chosen **per language** by the model router, for example:

| Language | Speech-to-text | Response model | Text-to-speech |
|---|---|---|---|
| Malayalam | IndicConformer, local | **your custom model** → Claude fallback | your custom TTS → Gemini TTS fallback |
| English | Whisper or IndicConformer | your custom model → Claude | Kokoro (fast, local) |
| French | Whisper | Claude | Gemini TTS |

### Emotion
- A speech emotion model runs on each finished utterance and gives a label (calm, happy, frustrated, angry, distressed, confused) and an intensity. Text sentiment from the transcript is a backup.
- The response model gets the emotion as a **style hint** ("caller is frustrated: be brief, acknowledge it, offer to connect"), and text-to-speech gets matching speaking-style controls where the voice supports them. Parler-TTS takes a style description; Gemini TTS takes style prompts.
- **Policy rules come before the model.** Distress, or anger that keeps rising across turns, means the engine transfers the call to you or takes an urgent message. That's the receptionist design's "rules first" principle.

## Pluggable model providers

Every model sits behind a small streaming interface, so a custom model is just one more
implementation:

```csharp
interface ISpeechToText  { IAsyncEnumerable<SttResult> Transcribe(IAsyncEnumerable<AudioFrame> audio, string? lang, CancellationToken ct); }
interface ILanguageId    { Task<LangResult> Detect(ReadOnlyMemory<byte> utterance, CancellationToken ct); }
interface IEmotion       { Task<EmotionResult> Detect(ReadOnlyMemory<byte> utterance, string transcript, CancellationToken ct); }
interface IResponseModel { IAsyncEnumerable<string> Respond(Conversation ctx, TurnHints hints, CancellationToken ct); }
interface ITextToSpeech  { IAsyncEnumerable<AudioFrame> Speak(string text, VoiceStyle style, string lang, CancellationToken ct); }
```

- **Your models run as an inference service on your GPU box.** It's the Python sidecars you already have, behind one streaming WebSocket or gRPC endpoint. The harness reaches it over a private, authenticated connection, so it never runs on the open internet.
- **Every provider gets a circuit breaker and a fallback chain**, the same pattern the harness uses for Claude. If your GPU box is slow or offline, the call falls back to a cloud model mid-call instead of going silent.
- **Routing is configuration:** for each tenant and each language, which provider and which voice. Swapping in a new fine-tuned model is a config change, not a code change.

## Where your custom models fit

| Kind of custom model | Plugs in as | Notes |
|---|---|---|
| Fine-tuned LLM (your style, your languages) | `IResponseModel` | Serve it with an OpenAI-compatible server (vLLM, llama.cpp, Ollama) so the adapter is tiny. It must stream tokens |
| Speech recognition fine-tuned on your speakers or dialects | `ISpeechToText` | Must support streaming or chunked input for low latency |
| Custom or cloned voice | `ITextToSpeech` | Must stream audio. Anything slower than real time (like Parler on an 8 GB GPU today) is only for pre-recorded prompts |
| Emotion classifier | `IEmotion` | Runs per utterance, so latency matters less |

**Hardware reality:** a live voice turn needs speech-to-text, the model and text-to-speech all
faster than real time, at once, for every concurrent call. An 8 GB GPU can do this for about one
call with small models. For more calls, or bigger models, rent a cloud GPU for the inference
service. The interfaces don't change.

## Training loop (data flywheel)

Every finished call can, **with consent**, produce training data: audio, transcript, the language
and emotion labels, and how the turn went (barge-ins, transfers, ratings). A nightly job (just
another queued job) exports a dataset for retraining your models. New model versions roll out
gradually per tenant, with the old version kept as a fallback.

Consent is announced at the start of the call, and recordings are encrypted and deleted on a
schedule. Canadian privacy law (PIPEDA) applies, plus each caller's own jurisdiction.

## Observability

Each turn records a latency breakdown: end of speech → final transcript → first model token →
first audio byte. It also records barge-in stop time, language switches, emotion labels and which
fallbacks fired. One trace ID covers the whole call. Dashboard alerts fire on p95 time-to-first-audio
and on the fallback rate.

## Build plan

| Phase | Delivers | Proves |
|---|---|---|
| **1. Voice gateway + barge-in** | Twilio Media Streams WebSocket in the harness, VAD, cancellable turn loop, Claude + a cloud STT/TTS | The real-time loop, interruptions under 200 ms, first audio under 1 s |
| **2. Your inference service** | Wrap your PC's sidecars (IndicConformer STT and language ID, TTS) behind one streaming endpoint; provider adapters + fallbacks | Malayalam calls on your own models |
| **3. Language + emotion** | Language ID on every utterance, per-language routing, emotion model, style hints, escalation rules | Mid-call language switching, tone changes, distress handling |
| **4. Your custom response model** | OpenAI-compatible adapter for your fine-tuned LLM, per-tenant routing, A/B against Claude | Custom-trained responses in live calls |
| **5. Flywheel** | Consented recordings → dataset export job → staged model rollout | Models improve from real calls |

The phone number moves from AgentPhone's built-in agent to **Twilio**, which can stream raw call
audio to the harness both ways. AgentPhone's hosted agent stays in place as the fallback until
phase 1 is live.
