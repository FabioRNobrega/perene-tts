# Agent Context

## Table of Contents
- [Project Overview](#project-overview)
- [Repository Map](#repository-map)
- [Architecture Summary](#architecture-summary)
- [Execution Environment](#execution-environment)
- [Coding Conventions](#coding-conventions)
- [Microsoft Learn MCP Server](#microsoft-learn-mcp-server)
- [Constraints](#constraints)
- [Available Commands](#available-commands)
- [Spec-Kit Workflow](#spec-kit-workflow)
- [Key Documentation](#key-documentation)

## Project Overview
Perene TTS is a local, unauthenticated voice studio. A .NET 10 ASP.NET Core Blazor Interactive Server host provides voice creation, saved-voice selection, text synthesis, durable pausable batch conversion of `.txt` folders into one MP3 per file, progress, and MP3 playback/download. A Python 3.11 FastAPI worker adapts the pinned Chatterbox Multilingual V3 implementation from the sibling book-notes-ia project. Supported studio languages are English (`en`), Portuguese (`pt`), and Swedish (`sv`). UI card layout and dark/paper-light appearance are inspired by the sibling perene-archive project.

## Repository Map
- `WebApp/` — .NET web project; Razor components, typed TtsClient, wire models, pure `BatchRules`, and authored CSS.
- `WebApp.Tests/` — xUnit project (net10.0) for `BatchRules` and `TtsClient` batch calls; run only in Docker by `make test`.
- `services/ChatterboxTtsService/` — Python worker, pytest tests, pinned requirements, test/runtime Docker stages.
- `scripts/mac-worker.sh` — native macOS virtualenv setup and auto/MPS worker launcher.
- `Specs/` — timestamped Requirements, Plan, and Validation per feature (foundation, batch text-to-audio jobs).
- `Dockerfile` — default SDK development/watch stage plus publish/build, `web-test` (xUnit), and non-root runtime stages.
- `docker-compose.yml` — local CPU web/worker stack and isolated test profile.
- `docker-compose.mac.yml` — Docker web host for a native macOS worker.
- `Makefile` — build/run/test/config-check and Mac commands.
- `.env.example`, `.gitignore`, `.dockerignore` — safe web-port configuration and private/generated file exclusions.
- `README.md` — user workflow, setup, persistence, architecture, and limitations.

## Architecture Summary
Development Compose mounts WebApp source into `/workspace/WebApp` and runs the .NET 10 SDK with `dotnet watch`; named bin/obj/NuGet volumes isolate generated output. Polling observes mounted-file changes and non-interactive mode restarts unsupported hot reload edits. `WebApp/Program.cs` registers Interactive Server components and a typed `TtsClient`. `MainLayout.razor` provides the PereneArchive-style sidebar, theme toggle, and active-batch status line. `Home.razor`, `GenerateAudio.razor`, `BatchAudio.razor`, and `Creations.razor` provide the four workflows; `BatchRow.razor`, `ConfirmDialog.razor`, and the shared `Pager.razor` support them. Circuit-scoped `StudioSession` polls health/jobs/batch summaries and keeps generation progress, selected voice, and text drafts across navigation (no batch conversion state lives in the circuit); `TtsClient` communicates with the worker using typed JSON models from `Contracts.cs`. The web `/audio/{id:guid}`, `/batches/{id:guid}/tracks/{number:int}`, and `/batches/{id:guid}/archive` endpoints stream worker files through one `ResponseHeadersRead` helper and handle download disposition. No database, queue, authentication, or frontend package is used.

`app/main.py` owns FastAPI routes and starts a background model load with interruptible 15/30/60-second retry backoff. Docker worker DNS uses configurable explicit resolvers, and its health/API is published only to localhost:5081. `StudioService` owns operation validation, jobs, FFmpeg conversion, voice names, and synthesis orchestration. `LocalVoiceStore` owns UUID-safe directories, checksums, archived references, and conditioning compatibility. `ChatterboxEngine` implements `SynthesisEngine` and owns pinned model/provider calls. `OperationGate` serializes model access (conditioning selection plus synthesis): at most one interactive operation, which reserves its slot in the request (second gets 409) and takes priority over batch chunks; it tracks `conditioning_owner`. Run exactly one Uvicorn worker. Latest job statuses are bounded in memory; completed voices/audio persist on disk. `GET /creations` reads completed MP3s and atomic JSON metadata sidecars, newest first; legacy files without metadata remain browsable. Creation metadata is published before the final MP3, so the listing only exposes completed audio files.

### Voice Creation
`InputFile` streams a bounded MP3 or WAV reference through `TtsClient` to `POST /voices`. A background job decodes mono PCM through FFmpeg, validates duration/peak, resolves the recording identity, prepares or loads compatible conditioning, saves a name sidecar, synthesizes predefined language text, and atomically publishes an MP3.

### Text Synthesis
`GenerateAudio.razor` submits a saved UUID and text to `POST /speech`. StudioService uses the saved voice language, validates/reloads conditioning through `select_voice`, chunks text through `chunk_text`, renders PCM, and publishes the job MP3 with the shared `encode_mp3` (192 kbps libmp3lame). The browser polls `/jobs/{uuid}` through TtsClient and uses the web audio route on completion. While a batch chunk holds the model, the job reports "Waiting for the batch to reach a safe point".

### Batch Conversion
`BatchAudio.razor` measures selected `.txt` files, orders them with `BatchRules.DefaultOrder`, and forwards them through `LazyBrowserFileStream` to `POST /batches`. `batch_project` validates names/limits/UTF-8; `BatchService` writes `<TTS_DATA_DIR>/batches/<uuid>/` (`manifest.json`, `sources/NNN.txt`) via a staging rename and owns every state transition under one lock (`queued`, `running`, `pausing`, `paused`, `stopping`, `stopped`, `failed`, `completed`; `InvalidTransition` → 409). `BatchRepository` (`batch_manifest`) saves manifests atomically (temp + fsync + re-validate + replace) with UUID path containment. One `BatchRunner` daemon thread, started in `lifespan`, converts batches FIFO, one chunk per `gate.batch_step()` with `seed_offset=i`, saves `work/NNN/chunk-*.wav` checkpoints with a `checkpoint.json` identity, assembles with `wav_assembly`, and publishes `tracks/NNN.mp3` with its SHA-256. Pause/stop are cooperative at chunk boundaries; `BatchService.recover()` re-queues interrupted batches on startup. `GET /creations` merges completed batch tracks (`kind: "batch"`, empty `text`) with studio creations. `TTS_BATCH_ENABLED=false` skips the runner and makes `POST /batches` return 503.

## Execution Environment
Use `make docker-build`, `make docker-run-bg`, `make docker-down`, `make test`, and `make docker-check` from the repo root. `make test` runs pytest in the worker test image, `dotnet test` on `WebApp.Tests` via the Dockerfile `web-test` stage, then compiles the runtime stage. Docker mode needs Compose and GNU Make, not host .NET/Python. Both docker-run and docker-run-bg use SDK watch mode; foreground output shows Blazor restore/build/listening logs. Web NuGet restore uses configurable WEB_DNS_PRIMARY/WEB_DNS_SECONDARY resolvers. The runtime stage remains a separately compiled non-root publication target. Windows uses Linux containers with WSL2 Make. The default web URL is http://localhost:8082, configurable with `WEB_PORT` in `.env`; worker API is also available on localhost:5081. Steam Deck uses CPU. Named volumes retain data/model cache across restarts.

Apple Metal/MPS requires native macOS: `make mac-setup`, `make mac-worker`, then `make mac-up` in another terminal. Docker Linux containers do not expose Apple MPS. Native mode requires Python 3.11 and FFmpeg, reaches the host through host.docker.internal, and uses separate ignored data/model directories. Do not equate automatic MPS selection with measured performance; target hardware verification remains necessary.

## Coding Conventions
- Use nullable-enabled C#, typed wire records with explicit snake_case JSON attributes, DI, and typed HttpClient. Explicit `RequiresAspNetWebAssets=true` is required because Docker restores the csproj before copying Razor files; keep `MapStaticAssets`, `Assets`, and `ImportMap` for published framework scripts.
- Keep browser workflow/form state in Razor components, HTTP/provider logic in focused services, pure presentation/ordering/action rules in static classes such as `Models/BatchRules.cs` (unit-tested in `WebApp.Tests`), and styles in `WebApp/wwwroot/app.css`. Use the exact Perene design-guide tokens, Montserrat for body text, and Zilla Slab for headings/logo. Fonts and Bootstrap Icons 1.13.1 are bundled in `wwwroot/vendor/` with their licenses; do not require external CDN requests for the UI.
- Python components use typed boundaries and the SynthesisEngine protocol (`synthesize(..., *, seed_offset=0)`). Batch modules stay single-responsibility (project, manifest, service, runner, wav_assembly, operation_gate). Tests use pytest, TestClient, the shared `FakeEngine` in `tests/conftest.py`, and real FFmpeg without model downloads. Web tests use xUnit with fake `HttpMessageHandler`s.
- Preserve UUID-generated paths, atomic audio/metadata/manifest publication, checksum/revision checks on conditioning and checkpoints, and the rule that runner writes never overwrite a user's `pausing`/`stopping` request.
- Batch track failures expose only `TrackError` messages; other errors map to a generic message so paths and internals stay in logs. Logs carry batch ID, track number, and chunk counters, never source text.
- Keep model internals and storage paths in worker logs; expose concise validation/recovery messages to users.

## Microsoft Learn MCP Server
Microsoft-stack detection finds `WebApp/WebApp.csproj` using Microsoft.NET.Sdk.Web and `net10.0`, plus `WebApp.Tests/WebApp.Tests.csproj` (Microsoft.NET.Test.Sdk, xUnit). Require Microsoft Learn MCP as the first-party documentation source for decisions involving .NET 10, ASP.NET Core, Blazor (including `InputFile` uploads), HttpClient, and `dotnet test` in this repository.
- Start with `microsoft_docs_search` to locate current official guidance.
- Use `microsoft_code_sample_search` when a decision depends on API usage or examples.
- Use `microsoft_docs_fetch` when complete prerequisites, version notes, or procedures are needed.
- Cite relevant Microsoft Learn URLs and summarize evidence in plans/reviews when it affects a technical decision.
- Reconcile Learn guidance with documented repo constraints; preserve the repo constraint unless the user approves a change, and document why.
- Do not rely only on model memory for Microsoft architecture/API/security/compatibility/version decisions. If the MCP server is unavailable, state that verification is pending.

## Constraints
- Keep this foundation local and simple: no login or public hosting is currently implemented.
- Preserve three allowed languages, MP3/WAV references, 20 MiB uploads, 3–60 second references, 80-character names, and 5,000-character text bounds unless changing the spec.
- Preserve batch bounds: 1–200 UTF-8 `.txt` files, 1–200,000 trimmed characters and 1 MiB each, 10 MiB total, 280-character chunks, 192 kbps MP3 output.
- Run one worker process and one batch runner thread to preserve shared model locking; do not add Uvicorn workers, parallel batches, or an external queue without redesigning orchestration.
- Never commit recordings, generated speech, model weights, `.venv`, `.env`, or build output.
- Never accept client-supplied filesystem paths or conditioning `.pt` uploads; uploaded batch file names are display labels only.
- Do not modify the sibling reference projects as part of Perene TTS work.
- Preserve pinned source/model revisions and update compatibility metadata when intentionally changing the model/schema.

## Available Commands
| Command | Purpose |
| --- | --- |
| `make docker-build` | Build web and runtime worker images. |
| `make docker-run` / `make docker-run-bg` | Start/build the CPU stack in foreground/background. |
| `make docker-down` | Stop, preserving named volumes. |
| `make docker-logs` / `make docker-ps` | Inspect running services. |
| `make test` | Run lightweight pytest container, xUnit `WebApp.Tests` (`web-test` stage), and compile the .NET runtime stage. |
| `make docker-check` | Validate standard/Mac Compose configs. |
| `make docker-reset` | Delete the stack and its data/model/key volumes; never use for routine fixes. |
| `make docker-shell` / `make docker-exec` | Open a .NET SDK shell. |
| `make dotnet ARGS="build"` | Run a .NET SDK command against mounted source. |
| `make get-url` | Print published UI and health URLs. |
| `make mac-setup` | Install native macOS worker environment. |
| `make mac-worker` | Launch native auto/MPS worker. |
| `make mac-up` / `make mac-down` | Start/stop web container for native worker. |
| `dotnet build WebApp/WebApp.csproj` | Optional native compilation with .NET 10 SDK. |

## Spec-Kit Workflow
Design documents live under `Specs/<YYYYMMDDHHMMSS>-<slug>/`: Requirements → Plan → Validation → implementation. The explicitly invoked new-spec/implement-spec/init-agent skills created the specifications and this context; there is no local slash-command/skill directory or spec-init Make target in this repo.

## Key Documentation
- [README.md](README.md)
- [Foundation requirements](Specs/20261008154201-tts-foundation/Requirements.md)
- [Foundation plan](Specs/20261008154201-tts-foundation/Plan.md)
- [Foundation validation](Specs/20261008154201-tts-foundation/Validation.md)
- [Batch jobs requirements](Specs/20261008195738-batch-text-to-audio-jobs/Requirements.md)
- [Batch jobs plan](Specs/20261008195738-batch-text-to-audio-jobs/Plan.md)
- [Batch jobs validation](Specs/20261008195738-batch-text-to-audio-jobs/Validation.md)
