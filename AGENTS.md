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
Perene TTS is a local, unauthenticated voice studio. A .NET 10 ASP.NET Core Blazor Interactive Server host provides voice creation, saved-voice selection, text synthesis, durable pausable batch conversion of `.txt` folders into one MP3 per file, progress, and MP3 playback/download. A Python 3.11 FastAPI worker adapts the pinned Chatterbox Multilingual V3 implementation from the sibling book-notes-ia project. Supported studio languages are English (`en`), Portuguese (`pt`), and Swedish (`sv`). The same host also runs as an offline, ad-hoc signed macOS desktop app (`Perene TTS.app`, Apple silicon) that bundles the .NET runtime, a standalone Python with the worker and its macOS wheels, model weights, and a minimal LGPL FFmpeg. It is built entirely in Docker; nothing is installed on the Mac. UI card layout and dark/paper-light appearance are inspired by the sibling perene-archive project.

## Repository Map
- `WebApp/` — .NET web project; `StudioApp` composition, Razor components, typed TtsClient, wire models, pure `BatchRules`/`DesktopRules`, and authored CSS.
- `WebApp/Desktop/` — desktop mode only: launch detection, paths, instance lock, worker sidecar, access middleware, Photino shell, AppKit hooks, close coordination.
- `WebApp.Tests/` — xUnit project (net10.0) for rules, `TtsClient`, `AudioExport`, local files, desktop paths/launch/ready line, and the access middleware; run only in Docker by `make test`.
- `services/ChatterboxTtsService/` — Python worker, pytest tests, pinned requirements, test/runtime Docker stages.
- `scripts/mac-worker.sh` — native macOS virtualenv setup and auto/MPS worker launcher.
- `packaging/macos/` — Docker build of the desktop app: builder `Dockerfile` (.NET SDK, Python, zig, rcodesign, llvm), `build.sh`, `macos-cc` (zig cross-compiler wrapper), `Info.plist.in`, `VERSION`, icon SVG and committed `AppIcon-1024.png`, `smoke.py`/`smoke.sb`, `How to open.txt`, `THIRD-PARTY-NOTICES.md`. Outputs go to ignored `build/macos/` (cache) and `dist/` (app and zip).
- `Specs/` — timestamped Requirements, Plan, and Validation per feature (foundation, batch text-to-audio jobs, macOS desktop app).
- `Dockerfile` — default SDK development/watch stage plus publish/build, `web-test` (xUnit), and non-root runtime stages.
- `docker-compose.yml` — local CPU web/worker stack and isolated test profile.
- `docker-compose.mac.yml` — Docker web host for a native macOS worker.
- `Makefile` — build/run/test/config-check and Mac commands.
- `.env.example`, `.gitignore`, `.dockerignore` — safe web-port configuration and private/generated file exclusions.
- `README.md` — user workflow, setup, persistence, architecture, and limitations.

## Architecture Summary
Development Compose mounts WebApp source into `/workspace/WebApp` and runs the .NET 10 SDK with `dotnet watch`; named bin/obj/NuGet volumes isolate generated output. Polling observes mounted-file changes and non-interactive mode restarts unsupported hot reload edits. `WebApp/Program.cs` registers Interactive Server components and a typed `TtsClient`. `MainLayout.razor` provides the PereneArchive-style sidebar, theme toggle, and active-batch status line. `Home.razor`, `GenerateAudio.razor`, `BatchAudio.razor`, and `Creations.razor` provide the four workflows; `BatchRow.razor`, `ConfirmDialog.razor`, and the shared `Pager.razor` support them. Circuit-scoped `StudioSession` polls health/jobs/batch summaries and keeps generation progress, selected voice, and text drafts across navigation (no batch conversion state lives in the circuit); `TtsClient` communicates with the worker using typed JSON models from `Contracts.cs`. The web `/audio/{id:guid}`, `/batches/{id:guid}/tracks/{number:int}`, and `/batches/{id:guid}/archive` endpoints stream worker files through one `ResponseHeadersRead` helper and handle download disposition. No database, queue, authentication, or frontend package is used.

### Desktop Mode (macOS app)
`Program.cs` branches to `DesktopHost.Run()` when `DesktopLaunch.ShouldRun` sees `--desktop` or an `….app/Contents/MacOS/` base directory; otherwise web mode builds through `StudioApp.AddStudio`/`MapStudio` exactly as before. Desktop mode runs synchronously on the main thread: `DesktopPaths` (Application Support/Logs/Caches, `0700`), `InstanceLock` (flock), `WorkerProcess` (spawns the bundled `Contents/Resources/python/bin/python3.11 -I -X pycache_prefix=<cache> -m app.desktop` with settings and a per-launch token in the environment, waits for `PERENE_WORKER_READY port=<n>`, logs to `worker.log`, stops by closing stdin, then kills the tree after 10 s), Kestrel on `127.0.0.1:0` with `AllowedHosts=127.0.0.1`, Data Protection keys in Application Support, and `DesktopAccessMiddleware` (per-launch `/?launch=` token → HttpOnly SameSite=Strict cookie; everything else 403). A Photino window (system WKWebView) shows the studio. Components use `IHostShell` (`BrowserHostShell` in web mode, `PhotinoHostShell` in desktop mode), `DownloadButton` + `AudioExport` (native save, stream to `.part`, rename), and `ISourceFile` (`BrowserSourceFile`/`LocalSourceFile`, `LocalSourceFiles.Enumerate`, `BoundedReadStream`). Photino 4.0.16 cannot cancel a close or suggest a save name on macOS, so `MacAppKit` adds `windowShouldClose:`/`applicationShouldTerminate:` to Photino's delegate classes and opens `NSSavePanel` directly; `CloseCoordinator` + `DesktopRules` decide, and `MainLayout` shows the `ConfirmDialog`. The worker's `app/desktop.py` binds a loopback socket, prints the ready line, serves `app.main:app` with one Uvicorn server, and exits on stdin EOF; `TTS_API_TOKEN` (`ApiToken` middleware), `TTS_MODEL_DIR` (`verify_bundled_model`, no `snapshot_download`), and `TTS_FFMPEG` are inert unless set.

`app/main.py` owns FastAPI routes and starts a background model load with interruptible 15/30/60-second retry backoff. Docker worker DNS uses configurable explicit resolvers, and its health/API is published only to localhost:5081. `StudioService` owns operation validation, jobs, FFmpeg conversion, voice names, and synthesis orchestration. `LocalVoiceStore` owns UUID-safe directories, checksums, archived references, and conditioning compatibility. `ChatterboxEngine` implements `SynthesisEngine` and owns pinned model/provider calls. `OperationGate` serializes model access (conditioning selection plus synthesis): at most one interactive operation, which reserves its slot in the request (second gets 409) and takes priority over batch chunks; it tracks `conditioning_owner`. Run exactly one Uvicorn worker. Latest job statuses are bounded in memory; completed voices/audio persist on disk. `GET /creations` reads completed MP3s and atomic JSON metadata sidecars, newest first; legacy files without metadata remain browsable. Creation metadata is published before the final MP3, so the listing only exposes completed audio files.

### Voice Creation
`InputFile` streams a bounded MP3 or WAV reference through `TtsClient` to `POST /voices`. A background job decodes mono PCM through FFmpeg, validates duration/peak, resolves the recording identity, prepares or loads compatible conditioning, saves a name sidecar, synthesizes predefined language text, and atomically publishes an MP3.

### Text Synthesis
`GenerateAudio.razor` submits a saved UUID and text to `POST /speech`. StudioService uses the saved voice language, validates/reloads conditioning through `select_voice`, chunks text through `chunk_text`, renders PCM, and publishes the job MP3 with the shared `encode_mp3` (192 kbps libmp3lame). The browser polls `/jobs/{uuid}` through TtsClient and uses the web audio route on completion. While a batch chunk holds the model, the job reports "Waiting for the batch to reach a safe point".

### Batch Conversion
`BatchAudio.razor` measures selected `.txt` files, orders them with `BatchRules.DefaultOrder`, and forwards them through `LazyBrowserFileStream` to `POST /batches`. `batch_project` validates names/limits/UTF-8; `BatchService` writes `<TTS_DATA_DIR>/batches/<uuid>/` (`manifest.json`, `sources/NNN.txt`) via a staging rename and owns every state transition under one lock (`queued`, `running`, `pausing`, `paused`, `stopping`, `stopped`, `failed`, `completed`; `InvalidTransition` → 409). `BatchRepository` (`batch_manifest`) saves manifests atomically (temp + fsync + re-validate + replace) with UUID path containment. One `BatchRunner` daemon thread, started in `lifespan`, converts batches FIFO, one chunk per `gate.batch_step()` with `seed_offset=i`, saves `work/NNN/chunk-*.wav` checkpoints with a `checkpoint.json` identity, assembles with `wav_assembly`, and publishes `tracks/NNN.mp3` with its SHA-256. Pause/stop are cooperative at chunk boundaries; `BatchService.recover()` re-queues interrupted batches on startup. `GET /creations` merges completed batch tracks (`kind: "batch"`, empty `text`) with studio creations. `TTS_BATCH_ENABLED=false` skips the runner and makes `POST /batches` return 503.

## Execution Environment
Use `make docker-build`, `make docker-run-bg`, `make docker-down`, `make test`, and `make docker-check` from the repo root. `make test` runs pytest in the worker test image, `dotnet test` on `WebApp.Tests` via the Dockerfile `web-test` stage, then compiles the runtime stage. Docker mode needs Compose and GNU Make, not host .NET/Python. Both docker-run and docker-run-bg use SDK watch mode; foreground output shows Blazor restore/build/listening logs. Web NuGet restore uses configurable WEB_DNS_PRIMARY/WEB_DNS_SECONDARY resolvers. The runtime stage remains a separately compiled non-root publication target. Windows uses Linux containers with WSL2 Make. The default web URL is http://localhost:8082, configurable with `WEB_PORT` in `.env`; worker API is also available on localhost:5081. Steam Deck uses CPU. Named volumes retain data/model cache across restarts.

The desktop app is built inside Docker on any machine with `make desktop-mac`. It downloads the macOS arm64 standalone Python, PyPI wheels and .NET runtime pack, cross-compiles FFmpeg with zig, ad-hoc signs with rcodesign, and zips the result. The first build needs internet and about 15 GB; `STEPS="<step> …"` reruns single steps. On the Mac, which needs no installs, `make desktop-smoke` runs `codesign --verify` and an offline worker check under `sandbox-exec` with the app's bundled Python.

Apple Metal/MPS requires native macOS: `make mac-setup`, `make mac-worker`, then `make mac-up` in another terminal. Docker Linux containers do not expose Apple MPS. Native mode requires Python 3.11 and FFmpeg, reaches the host through host.docker.internal, and uses separate ignored data/model directories. Do not equate automatic MPS selection with measured performance; target hardware verification remains necessary.

## Coding Conventions
- Use nullable-enabled C#, typed wire records with explicit snake_case JSON attributes, DI, and typed HttpClient. Explicit `RequiresAspNetWebAssets=true` is required because Docker restores the csproj before copying Razor files; keep `MapStaticAssets`, `Assets`, and `ImportMap` for published framework scripts.
- Keep browser workflow/form state in Razor components, HTTP/provider logic in focused services, pure presentation/ordering/action rules in static classes such as `Models/BatchRules.cs` (unit-tested in `WebApp.Tests`), and styles in `WebApp/wwwroot/app.css`. Use the exact Perene design-guide tokens, Montserrat for body text, and Zilla Slab for headings/logo. Fonts and Bootstrap Icons 1.13.1 are bundled in `wwwroot/vendor/` with their licenses; do not require external CDN requests for the UI.
- Python components use typed boundaries and the SynthesisEngine protocol (`synthesize(..., *, seed_offset=0)`). Batch modules stay single-responsibility (project, manifest, service, runner, wav_assembly, operation_gate). Tests use pytest, TestClient, the shared `FakeEngine` in `tests/conftest.py`, and real FFmpeg without model downloads. Web tests use xUnit with fake `HttpMessageHandler`s.
- Preserve UUID-generated paths, atomic audio/metadata/manifest publication, checksum/revision checks on conditioning and checkpoints, and the rule that runner writes never overwrite a user's `pausing`/`stopping` request.
- Batch track failures expose only `TrackError` messages; other errors map to a generic message so paths and internals stay in logs. Logs carry batch ID, track number, and chunk counters, never source text.
- Keep model internals and storage paths in worker logs; expose concise validation/recovery messages to users.
- Desktop concerns stay in `WebApp/Desktop/`; Razor components depend on `IHostShell`/`ISourceFile`, never on Photino. Pure desktop decisions (`DesktopLaunch`, `WorkerReadyLine`, `DesktopPaths.Resolve`, `DesktopRules`) are static and unit-tested. Native AppKit calls live only in `MacAppKit` and must run on the main thread.

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
- Never commit recordings, generated speech, model weights, `.venv`, `.env`, build output, `build/`, `dist/`, the `.app`, or its zip. `packaging/macos/AppIcon-1024.png` is the one committed generated asset.
- The desktop app never writes inside its bundle (that breaks the ad-hoc signature); tokens go to the worker through its environment, never argv; both servers bind only `127.0.0.1`.
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
| `make desktop-mac` | Build `dist/Perene TTS.app` and `dist/Perene-TTS-macos-arm64.zip` inside Docker (any machine; nothing installed on the Mac). |
| `make desktop-run` | Open the built app. |
| `make desktop-smoke` | On the Mac: signature check plus offline end-to-end check of the bundled worker (`DEVICE=cpu|mps|auto`). |
| `make desktop-icon` | Re-render `AppIcon-1024.png` from the SVG in Docker. |
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
- [macOS desktop app requirements](Specs/20261009104126-macos-desktop-app/Requirements.md)
- [macOS desktop app plan](Specs/20261009104126-macos-desktop-app/Plan.md)
- [macOS desktop app validation](Specs/20261009104126-macos-desktop-app/Validation.md)
