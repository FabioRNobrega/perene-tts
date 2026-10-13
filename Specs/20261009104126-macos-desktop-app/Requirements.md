# Requirements: macOS Desktop App (Offline, Ad-hoc Signed .app)

## Table of Contents
- [Problem Statement](#problem-statement)
- [Decisions Already Made](#decisions-already-made)
- [User Stories](#user-stories)
- [Functional Requirements](#functional-requirements)
- [Non-Functional Requirements](#non-functional-requirements)
- [Out of Scope](#out-of-scope)
- [Open Questions](#open-questions)

## Problem Statement
Perene TTS runs only as a Docker Compose stack. `make docker-run-bg` starts the .NET SDK web container (`Dockerfile` `development` stage) and the Python worker container (`services/ChatterboxTtsService/Dockerfile` `runtime` stage), and the user opens http://localhost:8082 in a browser. On a Mac, Metal acceleration also needs the native worker: `make mac-setup`, `make mac-worker` and `make mac-up` in two terminals.

This setup has four problems:
- **It needs Docker, GNU Make, a terminal, and on a Mac a Homebrew FFmpeg and a Python virtualenv.**
- **The first start needs the internet.** `ChatterboxEngine.load()` in `services/ChatterboxTtsService/app/engine.py` downloads the pinned `ResembleAI/chatterbox` weights (about 3.0 GB) through `snapshot_download`.
- **FFmpeg must come from the environment.** `studio.ffmpeg()` runs a bare `ffmpeg` from `PATH`.
- **The UI assumes a browser.** It relies on `<a download>` links (`GenerationOutput.razor`, `Creations.razor`, `BatchRow.razor`) and a `webkitdirectory` `InputFile` (`BatchAudio.razor`).

The user wants a Mac app they can double-click on their MacBook Air, and share with a few friends who also have Apple-silicon Macs. It must run fully offline with everything shipped inside, and must not depend on Docker, .NET, Python, FFmpeg, Homebrew or a model download. The user does not want a paid Apple Developer account. The existing Docker and `mac-*` workflows must keep working as they do today.

The target Mac was checked on 2026-10-12 (from the [Photino.NET on this Mac guide](https://claude.ai/code/artifact/b25d4ee0-3d2d-4a4f-8f5f-9451c0f7a7eb)):
- **Hardware:** MacBook Air (Mac16,12), Apple M4 with 10 cores (4 performance, 6 efficiency), native arm64 (`sysctl.proc_translated` = 0), 24 GB memory.
- **OS:** macOS 26.6.2 (build 25G83). Gatekeeper is enabled. Rosetta 2 is installed but not needed.
- **Tools:** Docker Desktop is installed. The .NET SDK is **not** installed, and none is needed: the build runs in Docker, and macOS's built-in `codesign` and `sandbox-exec` cover the checks on the Mac.
- **Web view:** Photino uses the system WKWebView, which ships with macOS. Nothing needs to be bundled for the UI to render or play MP3.

## Decisions Already Made
These were agreed with the user before writing this spec. They are not open for re-discussion here.

- **Target:** macOS on Apple silicon only (`osx-arm64`), packaged as `Perene TTS.app` inside a zip. There is no Intel or universal build. The Linux AppImage and Windows come later.
- **Audience:** the user's own Mac, plus a few friends with Apple-silicon Macs. The zip is shared by hand (AirDrop, a USB drive or a cloud-drive link). There is no public hosting, no CI and no store.
- **No paid Apple account:** the app is **ad-hoc signed** (with `rcodesign` in the builder, equivalent to `codesign --sign -`). It is not notarized and does not use the hardened runtime. On the user's Mac, a locally built app is not quarantined and opens directly. Friends approve the first launch once, through System Settings → Privacy & Security → **Open Anyway**.
- **Ship everything:** model weights, FFmpeg, the Python runtime and libraries, and the .NET runtime are all bundled. The web view is the system WKWebView. Nothing is downloaded at install or first run.
- **Worker:** the existing FastAPI/Uvicorn worker becomes one persistent sidecar process bound to `127.0.0.1` on a random port and protected by a per-launch token. Loopback HTTP needs no internet. There is no stdin/stdout RPC.
- **UI host:** the existing ASP.NET Core Blazor Interactive Server host runs in the desktop process on a random loopback port. A Photino window shows it. UI changes for native dialogs are acceptable.
- **Build in Docker, install nothing on the Mac** (changed 2026-10-12, replacing "build on the Mac"): `make desktop-mac` runs inside a builder container on any machine. The container holds every build tool (the .NET SDK, Python, zig, rcodesign, llvm). It downloads the macOS arm64 parts that already exist (a standalone Python, PyPI wheels, the .NET runtime pack), cross-compiles FFmpeg, signs, and writes the app and zip to `dist/` outside the container. PyInstaller is not used, because it cannot cross-compile. The Mac needs only Docker for building and nothing at all for running. Product code and tests are still developed and tested with `make test`.
- **Existing workflows stay supported:** `make docker-run-bg`, `make test` and the `mac-*` targets keep working unchanged.
- **Updates are manual:** the user or friend replaces the app in `/Applications`, and user data is kept.
- **First launch starts empty:** data from the Docker volumes or from `make mac-worker`'s `services/ChatterboxTtsService/data` is not imported.
- **Closing:** while a conversion is active, the window asks for confirmation, and it closes only if the user stops the conversion.
- **Model:** only `multilingual-v3` is bundled.
- **Identity:** the app is named "Perene TTS". Its icon is the Bootstrap `soundwave` glyph in Perene gold on a Perene green rounded square, following the macOS icon grid.

## User Stories
- Given the user's Mac with Wi-Fi off, when the user double-clicks **Perene TTS** in `/Applications`, then a "Perene TTS" window opens with the existing studio UI. The engine status moves from "Loading voice engine…" to "Voice engine ready · MPS" (or "· CPU", per the device decision) without any download.
- Given a friend's Apple-silicon Mac on macOS 14 or later, when the friend unzips the shared zip, drags **Perene TTS** to Applications and double-clicks it, then macOS blocks the first launch. After the friend selects **Open Anyway** in System Settings → Privacy & Security once, the app opens and works offline, and later launches open directly.
- Given the app is open, when the user creates a voice, generates speech and runs a batch, then everything works as in the Docker version and data is saved under `~/Library/Application Support/Perene TTS/`.
- Given a completed creation or batch, when the user selects a download control, then a native "Save" sheet opens with a sensible file name, and the MP3 or ZIP is written where they choose.
- Given a batch is converting, when the user closes the window, then a confirmation explains that the conversion must be stopped. The window closes only after the user selects "Stop conversion and quit"; "Keep working" leaves everything running.
- Given the app is already open, when the user double-clicks it again, then macOS brings the open window to the front and no second voice engine starts.

## Functional Requirements

**Worker (`services/ChatterboxTtsService/app/`)**

1. FR1 — Add a desktop entry point, `app/desktop.py`.
   - It binds a TCP socket on `127.0.0.1` with an OS-assigned port.
   - It writes exactly one line, `PERENE_WORKER_READY port=<n>`, to stdout.
   - It then serves the existing `app.main:app` with a single Uvicorn server on that socket. It never binds `0.0.0.0` and never starts more than one worker.
2. FR2 — When `TTS_API_TOKEN` is set, every HTTP request whose `X-Perene-Token` header does not match it gets `401` with a concise `detail`. The comparison is constant-time. When the variable is unset (Docker and `mac-worker` modes), behaviour is unchanged.
3. FR3 — The desktop entry point watches stdin. When stdin reaches end-of-file (the parent closed the pipe or died), it starts a graceful Uvicorn shutdown, which runs the existing lifespan cleanup: `runner.stop`, then `loader.join`.
4. FR4 — When `TTS_MODEL_DIR` is set, `ChatterboxEngine.load()` loads `multilingual-v3` from that directory with `ChatterboxMultilingualTTS.from_local` and never calls `snapshot_download`.
   - It first checks the directory's `model-manifest.json`: the repository must be `ResembleAI/chatterbox`, the revision must equal `MODEL_REVISION`, and every file in `MODEL_FILES` must be present with the recorded size.
   - On any mismatch, `/health` reports `load_error` as "The bundled voice model is missing or damaged. Reinstall Perene TTS."
5. FR5 — `studio.ffmpeg()` runs the executable named by `TTS_FFMPEG`, defaulting to `ffmpeg`. The bundled FFmpeg supports every command used today:
   - reference decode (`-f mp3|wav` to mono 24 kHz `pcm_s16le` WAV, `-protocol_whitelist file,pipe`)
   - 192 kbps `libmp3lame` encoding.
6. FR6 — The packaged worker creates a voice, synthesizes speech and completes a batch with outbound networking denied, making no outbound connection attempts. The packaged launch environment sets `HF_HUB_OFFLINE=1` and `TRANSFORMERS_OFFLINE=1`.

**Desktop host (`WebApp/`)**

7. FR7 — `WebApp` runs in desktop mode when started with `--desktop`, or when it runs from inside an app bundle (its base directory is `…/<name>.app/Contents/Resources/dotnet/`, reached from the `Contents/MacOS/WebApp` launcher), because Finder cannot pass arguments. In desktop mode it:
   - binds Kestrel only to `127.0.0.1` with port `0` and reads the bound address through `IServerAddressesFeature`
   - sets `AllowedHosts` to `127.0.0.1`
   - opens a Photino window titled "Perene TTS" that loads that address.

   Otherwise `Program.cs` behaves exactly as today.
8. FR8 — In desktop mode, every request to the web host must carry an HttpOnly, SameSite=Strict per-launch session cookie. Requests without it get `403`. The cookie is issued only when the Photino window first loads `/?launch=<token>`. Other local processes and browser tabs therefore cannot drive the studio.
9. FR9 — Before starting the web host, desktop mode launches the bundled worker.
   - The worker gets: `TTS_API_TOKEN`, `TTS_DATA_DIR`, `TTS_MODEL_DIR`, `TTS_FFMPEG`, `TTS_DEVICE` (see Open Questions), `PYTORCH_ENABLE_MPS_FALLBACK=1`, `TTS_BATCH_ENABLED=true`, the offline flags, and `HF_HOME`/`NUMBA_CACHE_DIR` pointing at the per-user cache.
   - The host waits for the ready line, then sets `TtsClient`'s base address and `X-Perene-Token` header.
   - If no ready line arrives within 120 seconds, or the worker exits first, a native alert names the log file (`~/Library/Logs/Perene TTS/worker.log`) and the app exits.
10. FR10 — If the worker exits while the window is open, the UI keeps showing the existing "voice engine is unavailable" state. The message changes to "The voice engine stopped unexpectedly. Close and reopen Perene TTS." The exit code is logged.
11. FR11 — Desktop mode holds an exclusive lock on `~/Library/Application Support/Perene TTS/instance.lock`. A second launch that gets past Launch Services (for example from Terminal) shows the native alert "Perene TTS is already running." and exits before starting a worker or web host. The OS releases the lock when the process ends, even after a crash.
12. FR12 — All writable state lives in the standard per-user macOS directories. The app bundle, the current directory and `~/.aspnet` are never written. Writing inside the bundle would also break its signature.

    | What | Where |
    | --- | --- |
    | Studio data (`TTS_DATA_DIR`) | `~/Library/Application Support/Perene TTS/studio` |
    | Data Protection keys | `~/Library/Application Support/Perene TTS/keys` |
    | Logs | `~/Library/Logs/Perene TTS` |
    | Caches | `~/Library/Caches/Perene TTS` |

    Directories are created with mode `0700`. The first launch starts with empty studio data. Replacing the app keeps all data.
13. FR13 — Closing the window (red close button or ⌘W) while a batch is `queued`, `running` or `pausing` (rules in `BatchRules`) is blocked. ⌘Q and Dock → Quit follow the same rules when Phase 1a shows Photino routes them through its closing handler (see Open Questions for the fallback).
    - The app shows the in-app `ConfirmDialog`. It names the active batches and explains that they will be stopped, that completed tracks are kept, and that Retry resumes the rest later.
    - "Stop conversion and quit" calls `POST /batches/{id}/stop` for each such batch. Once each is `stopping` or `stopped`, the app shuts down. "Keep working" cancels the close.
    - While an interactive voice or speech generation is running, a confirmation warns that it will be lost, with "Discard and quit" and "Keep working".
    - With no active work, the window closes at once and the app quits. The app does not stay running without a window.
14. FR14 — On shutdown, the host closes the worker's stdin (FR3). It waits up to 10 seconds, then kills the worker process tree. On the next launch, the existing `BatchService.recover()` finishes any `stopping` batch as `stopped`.
15. FR15 — In desktop mode, every download control opens a native save dialog: Download MP3 (`GenerationOutput.razor`, `Creations.razor`, `BatchRow.razor` track) and Download all ZIP (`BatchRow.razor`).
    - Suggested names: `perene-<id>.mp3`, `<Batch name> NNN.mp3` from `BatchRules.OutputName`, and `<Batch name>.zip`.
    - The dialog starts in `~/Music`, falling back to the home directory.
    - The file is streamed from the worker to `<path>.part` and then renamed. Cancelling writes nothing.
    - Success shows an accessible status notice naming the file. Failure shows a concise error and removes the partial file.
    - Web mode keeps the current `<a download>` links.
16. FR16 — In desktop mode, file and folder selection uses native dialogs, with the same limits as today and the same ordering, validation and upload path (multipart to the worker).
    - Batch audio: "Choose folder" opens a folder dialog and collects `.txt` files recursively without following symlinked directories. "Choose files" opens a multi-select `.txt` dialog.
    - Create a voice: a single-file dialog filtered to `.mp3`/`.wav`.
    - Only file contents and display names go to the worker. Filesystem paths never do.

**Packaging (`packaging/macos/`, `Makefile`)**

17. FR17 — `make desktop-mac` builds the builder image (`packaging/macos/Dockerfile`) and runs `packaging/macos/build.sh` in it, on any Docker host (Apple silicon or x86-64). It produces `dist/Perene TTS.app` and `dist/Perene-TTS-macos-arm64.zip`. Every download is pinned and checksum-verified, and cached in `build/macos/`. The app contains:
    - a self-contained `osx-arm64` .NET publish of `WebApp`
    - a standalone CPython 3.11 for macOS arm64 (python-build-standalone) with the worker (`app/`) and its pinned macOS arm64 wheels in `site-packages`, run as `python3.11 -I -m app.desktop`
    - the pinned `multilingual-v3` weights plus `model-manifest.json`
    - a minimal LGPL FFmpeg cross-compiled for arm64 with zig
    - `Info.plist`, `AppIcon.icns`, and third-party license notices.

    The build prints the size of each component, the app and the zip.
18. FR18 — **Compatibility floor:** every Mach-O file in the app is arm64 and declares a minimum macOS of 14.0 or lower, matching `LSMinimumSystemVersion` 14.0 (the oldest macOS that .NET 10 supports). The build fails if any file declares a newer minimum, is x86_64-only, or links a library outside the bundle or the system (for example `/opt/homebrew` or `/usr/local`).
19. FR19 — **Signing without a paid account:** `rcodesign sign` ad-hoc signs every nested Mach-O file and then the bundle, without the hardened runtime or entitlements. On the Mac, Apple's built-in `codesign --verify --deep --strict` passes; `make desktop-smoke` runs it. Nothing writes into the bundle at runtime: Python bytecode goes to `~/Library/Caches/Perene TTS/pycache` through `-X pycache_prefix`.
20. FR20 — `Info.plist` sets: `CFBundleName` and `CFBundleDisplayName` "Perene TTS", `CFBundleIdentifier` `local.perene.tts`, `CFBundleExecutable` `WebApp`, `CFBundleIconFile` `AppIcon`, `CFBundlePackageType` `APPL`, `CFBundleShortVersionString`/`CFBundleVersion` from `packaging/macos/VERSION`, `LSMinimumSystemVersion` 14.0, `LSApplicationCategoryType` `public.app-category.music`, `NSHighResolutionCapable` true, and `NSAppTransportSecurity` → `NSAllowsLocalNetworking` true (loopback HTTP in WKWebView).
21. FR21 — The icon source is `packaging/macos/perene-tts.svg`: the Bootstrap Icons 1.13.1 `soundwave` path in Ochre Gold on a Perene green rounded square, sized to the macOS app-icon grid with transparent margins. `make desktop-icon` renders `packaging/macos/AppIcon-1024.png` from it in Docker, and that PNG is committed. `make desktop-mac` builds `AppIcon.icns` from the PNG with Pillow in the builder. Finder, the Dock and ⌘-Tab show the icon.
22. FR22 — The zip contains the app and a short `How to open.txt` with the unzip, Applications and Open Anyway steps. A quarantined copy (as a friend receives it) opens after Open Anyway, and the worker and FFmpeg subprocesses start normally.
23. FR23 — `make desktop-smoke` (on the Mac, with nothing installed) first runs `codesign --verify --deep --strict` on the app, then uses the app's own bundled Python to run the worker from the built app with outbound network denied except loopback. It waits for `model_ready`, creates a voice from a generated 5-second WAV, synthesizes a short text, and checks that a non-empty MP3 is returned. It prints the device and timings, and fails on any error.
24. FR24 — The existing workflows are unchanged: `make docker-build`, `make docker-run-bg`, `make test`, `make docker-check` and the `mac-*` targets behave exactly as before, and all existing pytest and xUnit tests pass.
25. FR25 — README.md gets a "Desktop app (macOS)" section covering build, install, sharing with friends (Open Anyway and the Terminal alternative), data locations, update, uninstall, logs and limitations. AGENTS.md is refreshed through `init-agent`. The measured app size, zip size, worker bundle size, time to window, time to `model_ready`, speech speed on CPU and MPS, and peak memory are recorded in this spec's Validation results.

## Non-Functional Requirements
- **Offline:** after the app is built, nothing contacts the network: no model download, no NuGet or pip, no CDN. The UI already bundles its fonts and icons in `wwwroot/vendor/`.
- **Security:**
  - Both servers bind only to `127.0.0.1` with random ports.
  - The worker requires the per-launch token, and the web host requires the per-launch cookie plus host filtering against DNS rebinding.
  - Tokens are passed to the worker through its environment, never through `argv` (which other processes can read via `ps`).
  - Uploaded file names stay display-only, and conditioning `.pt` uploads stay impossible.
  - Without the hardened runtime, macOS does not block library injection into the app. This is accepted for a personal, hand-shared build and recorded in the README.
- **Compatibility:** Apple-silicon Macs (M1 or later) on macOS 14 Sonoma or later. Minimum memory and free disk space are taken from the measurements and stated in the README.
- **Repository constraints preserved:** one worker process and one batch runner thread. Pinned source and model revisions plus compatibility metadata stay unchanged. Writes stay atomic, UUID-safe paths stay, and the runner never overwrites a user's `pausing`/`stopping` request.
- **Responsiveness:** the window appears within 10 seconds of a double-click. Model loading continues in the background behind the existing "Loading voice engine…" status. Time to `model_ready` is measured and recorded, not gated.
- **Design (SOLID):**
  - Desktop concerns (launch detection, paths, lock, worker process, Photino shell, close coordination) live in focused classes under `WebApp/Desktop/`.
  - Components depend on a small `IHostShell` abstraction, not on Photino.
  - Pure decisions (desktop launch detection, close rules, ready-line parsing, path resolution) are static and unit-tested.
  - Web mode registers a browser implementation, so no component branches on Photino types.
- **Testability:** new worker behaviour is covered by pytest with the shared `FakeEngine` and no model download. New .NET rules and services are covered by xUnit in `WebApp.Tests` with fake `HttpMessageHandler`s and temp directories. Everything runs through `make test` on any machine. Only packaging and the window need the Mac.
- **Licensing:** sharing the zip with friends is distribution. Bundled third-party components ship their license texts and notices in `Contents/Resources/licenses/`: FFmpeg (with a source pointer), LAME, Python, PyTorch, Chatterbox, the model weights, Photino, .NET, fonts and icons.

## Out of Scope
- The Linux AppImage (the earlier target of this spec), Windows, and Intel or universal macOS builds. The worker and host work (FR1–FR16) is reusable for them later.
- A paid Apple Developer account, Developer ID signing, notarization, the hardened runtime, the App Sandbox and the Mac App Store.
- `.pkg` installers, Homebrew casks, Sparkle or any automatic updates and update checks.
- CI and any public release hosting.
- Importing voices, creations or batches from the Docker `tts-data` volume or from `make mac-worker` data.
- CUDA inference and the `nano` model.
- Automatic worker restart after a crash.
- Menu-bar extras or running in the background after the window closes.
- Renaming the in-app sidebar brand text "PereneTTS".
- Replacing Blazor Interactive Server with BlazorWebView/Photino.Blazor.

## Open Questions
- ⚠️ TODO: **Model redistribution (blocks sharing, not personal use).** Confirm from the model card at the pinned revision `5bb1f6ee58e50c3b8d408bc82a6d3740c2db6e18` that the weights' license allows redistribution inside the app, and record it in the notices before the zip goes to friends. The model card at that revision declares `license: mit` (recorded in `THIRD-PARTY-NOTICES.md`); the user still confirms.
- ⚠️ TODO: **Default device.** Proposal: `TTS_DEVICE=auto` (Metal/MPS with `PYTORCH_ENABLE_MPS_FALLBACK=1`, CPU otherwise), as `make mac-worker` does today. Ship `auto` only if Phase 1b shows the MPS build passes the smoke test and is not slower than CPU on the M4; otherwise ship `cpu`. The measured result needs the user's confirmation.
- ⚠️ TODO: **⌘Q and Dock → Quit.** If Phase 1a shows Photino does not route app termination through its closing handler, quitting cannot be confirmed. Proposed fallback: ⌘Q shuts down without a dialog (FR14), and interrupted batches are re-queued on the next launch by the existing `BatchService.recover()`. This needs the user's approval before Phase 3.
- ⚠️ TODO: **Exact icon colours.** The proposal is background `#1B2A22` (the Perene dark-green `--bg-elevated` token) and bars Ochre Gold 300 `#D8AC5C`. Alternatives: Signal Green 900 `#10291B` for the background, or the brighter Ochre Gold 200 `#E7C787` for the bars.
- ⚠️ TODO: **Size budget.** No limit is set yet. Expect an app of about 5 GB and a somewhat smaller zip. If the zip is too large to share comfortably (for example over a cloud-drive link), decide whether to trim the worker further.
