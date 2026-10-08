# Requirements: Perene TTS Foundation

## Table of Contents
- [Problem Statement](#problem-statement)
- [User Stories](#user-stories)
- [Functional Requirements](#functional-requirements)
- [Non-Functional Requirements](#non-functional-requirements)
- [Out of Scope](#out-of-scope)
- [Open Questions](#open-questions)

## Problem Statement
The existing `book-notes-ia/services/ChatterboxTtsService` is a Python CPU proof of concept with fixed WAV reference slots and predefined text. It lacks browser uploads, named voices, arbitrary text synthesis, MP3 delivery, and a standalone Blazor frontend. The empty `perene-tts` directory needs an independent application grounded in that service and the .NET 10 Blazor/card styling conventions of `perene-archive`.

## User Stories
- Given an MP3 or WAV reference, when I name a voice and select English, Portuguese, or Swedish, then the application saves it and generates a predefined sample.
- Given a saved voice, when I enter text, then I can play and download generated MP3 audio.
- Given saved voices, when Docker restarts, then they remain available.
- Given an Apple Silicon Mac, when I use the native worker mode, then inference selects MPS when available.

## Functional Requirements
1. FR1 — Provide a .NET 10 Interactive Server Blazor application with separate responsive voice-creation, text-generation, and creation-library pages, using the PereneArchive sidebar and Perene design-guide typography/colors.
2. FR2 — Accept bounded MP3 or WAV references, a name, and one of `en`, `pt`, or `sv`; validate decoded audio before creating a voice.
3. FR3 — Archive named voices, reference recordings, and compatible conditioning permanently; identical decoded references in the same language reuse the existing identity.
4. FR4 — Generate a fixed, language-specific preview after successful voice preparation.
5. FR5 — Generate speech from bounded user text using a selected voice in its saved language.
6. FR6 — Provide MP3 playback and downloads for previews and generated speech.
7. FR7 — Expose readiness, per-job progress, busy, failed, empty, and loading states; serialize inference to avoid voice contamination.
8. FR8 — Run web and worker containers through Docker Compose on Linux, macOS, and Windows with persistent volumes and Make build/run/test commands.
9. FR9 — Provide a Docker frontend/native macOS worker mode that selects Metal/MPS where available, with CPU fallback and clear device reporting.
10. FR10 — Document architecture and commands in README.md and root AGENTS.md, including Microsoft Learn guidance.

11. FR11 — Browse and search all saved audio and voices after restarts, including older MP3s; play/download audio and reuse voices from the library. Use the microphone PereneTTS logo and the requested English voice-test sentence with localized Portuguese/Swedish equivalents.

## Non-Functional Requirements
- Local single-user application without authentication; publish web only on localhost and publish the worker only on localhost in Docker mode.
- Upload limit 20 MiB; reference duration 3–60 seconds, decoded into mono 24 kHz PCM. Reject silence, unreadable audio, invalid language/UUID, and text over 5,000 characters.
- Server-generated storage paths; no uploaded conditioning, arbitrary filesystem paths, or shell interpolation of user input.
- Provider-specific inference stays behind the existing SynthesisEngine protocol; tests use fakes rather than model downloads.
- Preserve pinned Chatterbox source/model revisions and restricted conditioning loading. Voice selection and synthesis share a process lock.
- No changes to source projects. Runtime recordings, model weights, and audio are ignored by Git.

## Out of Scope
Authentication, public hosting, multi-user isolation, audiobook workflows, voice deletion, NVIDIA/AMD GPU configuration, and production availability guarantees.

## Open Questions
- Native MPS performance and Linux ARM64 dependency compatibility require verification on target hardware; this Steam Deck cannot establish those results.
