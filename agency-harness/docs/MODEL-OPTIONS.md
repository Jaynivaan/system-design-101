# Model options for the voice engine (Hugging Face, November 2026)

A survey of the Hugging Face Hub, run 2026-11-15, to find models for the voice engine
([VOICE-ENGINE.md](VOICE-ENGINE.md)). Malayalam comes first, on an 8 GB GPU, with cloud GPUs as an
option. Everything here is from model cards and Hub metadata; nothing was downloaded or tested.
**Licenses marked "check" were not confirmed on the Hub. Check them before any commercial use.**

## What to try first on the 8 GB GPU

| Need | Model | Why | License |
|---|---|---|---|
| End of the caller's turn | `adimyth/indic-smart-turn` (int8, CPU) + Silero VAD | Trained on real Indian phone calls, 11 Indic languages + English, under 50 ms on one CPU core. Replaces the "wait 600 ms of silence" rule | BSD-2 |
| Speech-to-text + language + emotion in one pass | `Prahaladha/hima-asr` (ONNX int8, CPU) | Malayalam with a "mixed" mode that keeps English words in Latin script (good for code-mixing), plus language ID and zero-shot emotion/intent from the same pass. 16–35× faster than real time on CPU | Apache-2.0 + Gemma terms |
| Speech-to-text (baseline) | `ai4bharat/indic-conformer-600m-multilingual` (already on your PC) | Proven on your machine; keep it as the fallback | MIT |
| Malayalam voice (fast) | `Oomni/mithi-v1-flash` | 99M parameters, Malayalam, about 3 s of CPU for 7 s of speech. GPU speed is likely much better. Only 2 voices, no cloning | Apache-2.0 |
| Malayalam voice with emotion control | `Zyphra/ZONOS3-Mini` | Streaming, voice cloning, word-level emotion and pace control; claims real time on laptops. Malayalam quality is unproven, so do a listening test | Apache-2.0 |
| Response model to fine-tune | `google/gemma-4-E4B-it` | Malayalam among 140+ languages; QLoRA fine-tune fits 8 GB; serve as Q4 GGUF with llama.cpp (OpenAI-compatible) | Apache-2.0 |

Gemini TTS stays as the quality fallback for Malayalam speech.

## Worth a cloud GPU

| Model | What it adds |
|---|---|
| `maya-research/maya2-native` | Best streaming Malayalam voice: 80 ms frames, WebSocket and OpenAI-style API, zero-shot voice cloning, about 111 ms to first audio under load. Needs about 9 GB at peak, so it doesn't fit your card |
| `sarvamai/sarvam-30b`, `gnani/gnani-evon-v3.3-30B-A3B`, `bharatgenai/Param2-17B-A2.4B-Thinking` | Strong Indic LLMs (mixture-of-experts, few active parameters). Use them as larger response models, or as "teachers" to generate training data for your own fine-tune |
| `nvidia/nemotron-3.5-asr-streaming-0.6b` | True streaming speech-to-text for English and French (license: check) |

## Not ready for this use

- **Speech-to-speech in one model** (LFM3-Omni, MiniCPM-o 4.5, Sophon-Realtime): none of them handles Malayalam yet. Keep the step-by-step pipeline.
- **Non-commercial models**: MMS-TTS (on your PC), `bharatgenai/Shrutam-2`, `gradium/phonon-40m-multilingual`. Fine for experiments, not for a paid product.
- **Licenses to check first**: LiveKit's turn-detector license, LFM 1.0, `bodhan-ai/indic-transcribe-core` (gated), `ai4bharat/IndicF5` (gated; may be research-only), `k2-fsa/OmniVoice`.

## Emotion

`Prahaladha/hima-asr` gives emotion in the same pass as transcription. `emotion2vec/emotion2vec_plus_large`
(ONNX port: `Voldorn/emotion2vec-plus-large-onnx`) is the most language-independent dedicated model.
None of these was trained on Malayalam emotion data, so check them against your own calls before you
trust them for escalation. Until then the harness uses simple rules on the transcript.

## How these plug in

Every model above fits one of the harness's provider slots. Most can be served behind the HTTP shapes
the adapters already speak:

- an OpenAI-style `/v1/audio/speech` for voices
- raw WAV in, JSON out for speech-to-text
- OpenAI-compatible `/v1/chat/completions` with `stream=true` for the response model

The ones that stream (ZONOS3, maya2) will get streaming adapters when we wire them in.
