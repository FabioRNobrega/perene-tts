# Plan: macOS Desktop App (Offline, Ad-hoc Signed .app)

## Table of Contents
- [Summary](#summary)
- [Technical Approach](#technical-approach)
  - [Delivery phases and gates](#delivery-phases-and-gates)
  - [Process model](#process-model)
  - [Worker changes](#worker-changes)
  - [Desktop host in WebApp](#desktop-host-in-webapp)
  - [Shell abstraction and UI changes](#shell-abstraction-and-ui-changes)
  - [Closing the window](#closing-the-window)
  - [Directories, logs, and single instance](#directories-logs-and-single-instance)
  - [Packaging the .app and DMG](#packaging-the-app-and-dmg)
  - [Signing and sharing without a paid account](#signing-and-sharing-without-a-paid-account)
  - [Icon](#icon)
  - [Design principles applied](#design-principles-applied)
- [Component Breakdown](#component-breakdown)
- [Dependencies](#dependencies)
- [External / Vendor Documentation Evidence](#external--vendor-documentation-evidence)
- [Flow](#flow)
- [Risk Assessment](#risk-assessment)

## Summary
Desktop mode is a second host mode of the existing `WebApp`. It starts from inside `Perene TTS.app` (or with `--desktop`) and does four things:
- locks a single instance
- launches the PyInstaller-packaged FastAPI worker as a loopback sidecar protected by a per-launch token
- runs the unchanged Blazor Interactive Server UI on a random loopback port
- shows the UI in a Photino window, which uses the system WKWebView.

Everything (the .NET runtime, worker, model weights and FFmpeg) ships in one ad-hoc signed `.app`, built natively on the Mac by `make desktop-mac` and wrapped in a DMG for friends. The Docker and `mac-*` workflows keep their current composition through a shared `StudioApp` registration.

Compared with the earlier Linux AppImage version of this plan, the worker and host work is the same. The bundled WebKitGTK and its spike gate are gone, because macOS provides WKWebView. The new work is the native build script, Mach-O compatibility checks and ad-hoc signing.

## Technical Approach

### Delivery phases and gates
1. **Phase 1: spikes on the Mac. Gates; nothing is merged into the product before they pass.**
   - **1a. Window and lifecycle.** Build a throwaway `packaging/macos/spike/` app: a self-contained `net10.0` `osx-arm64` console app with Photino.NET 4.0.16 and an in-process Kestrel on `127.0.0.1:0`, wrapped in an ad-hoc signed `.app`. On this Mac it must:
     - load `http://127.0.0.1:<port>` in WKWebView (App Transport Security with `NSAllowsLocalNetworking`)
     - set and send a SameSite=Strict, HttpOnly cookie from `/?launch=<token>`
     - play an MP3 through `<audio>` (the existing `AudioPlayer.razor` path)
     - open the native save, open-file and open-folder dialogs
     - cancel a close through `RegisterWindowClosingHandler` for the red button and ⌘W, and **record whether ⌘Q and Dock → Quit go through the same handler**
     - show a native alert before any window exists (for "already running" and worker errors)
     - not trigger a macOS firewall prompt for loopback-only listeners.

     Also record `file libPhotino.Native.dylib` (it must be arm64). **If ⌘Q bypasses the handler, stop and ask the user about the fallback** (Open Questions).
   - **1b. Worker, signing and sharing.** Build the PyInstaller `onedir` worker plus the model directory plus FFmpeg, place them in the spike `.app`, sign it inside-out and verify it. Then:
     - run FR23's smoke script with outbound network denied, on `cpu` and on `mps`, and record bundle size, cold start, time to `model_ready`, speech speed and peak RSS for each
     - run the Mach-O compatibility check (FR18)
     - copy the app with `ditto`, add a quarantine attribute as a download would, open it, approve **Open Anyway**, and confirm the worker and FFmpeg subprocesses start.

     If a friend's Mac is available, repeat the quarantine test there. **If subprocesses are blocked after Open Anyway, stop and ask the user** whether the README may require `xattr -dr com.apple.quarantine` instead.
2. **Phase 2: worker changes** (FR1–FR6) with pytest. Can run on any machine through `make test`.
3. **Phase 3: desktop host and UI** (FR7–FR16) with xUnit. Can run on any machine through `make test`, with the window checked on the Mac.
4. **Phase 4: packaging, docs, manual verification on the Mac** (FR17–FR25), plus a friend's Mac when available.

### Process model
```
Perene TTS.app (read-only, ad-hoc signed)
└─ Contents/MacOS/WebApp  (CFBundleExecutable; desktop mode detected from the bundle path)
   ├─ InstanceLock          ~/Library/Application Support/Perene TTS/instance.lock
   ├─ WorkerProcess ─ spawn Contents/MacOS/worker/perene-worker   (stdin pipe kept open)
   │      env: TTS_API_TOKEN, TTS_DATA_DIR, TTS_MODEL_DIR, TTS_FFMPEG, TTS_DEVICE,
   │           PYTORCH_ENABLE_MPS_FALLBACK=1, HF_HUB_OFFLINE=1, TRANSFORMERS_OFFLINE=1,
   │           HF_HOME, NUMBA_CACHE_DIR
   │      stdout line 1 → "PERENE_WORKER_READY port=41873" ; rest → ~/Library/Logs/Perene TTS/worker.log
   ├─ Kestrel 127.0.0.1:0  (StudioApp services + endpoints, AllowedHosts=127.0.0.1, launch cookie)
   └─ PhotinoWindow (WKWebView) → http://127.0.0.1:<web-port>/?launch=<token>   (main thread, WaitForClose)
```
There is still exactly one worker process and one batch runner thread, as AGENTS.md requires. The worker HTTP API, `TtsClient` and every Razor page stay the same. Only the base address, a header, and a few download and picker controls change in desktop mode.

### Worker changes
- **`app/desktop.py` (new; this is the PyInstaller entry script):**
  1. Create the socket (`socket.create_server(("127.0.0.1", 0))`) and print `PERENE_WORKER_READY port=<n>` with flush. The socket already accepts connections into its backlog, so the host may connect before Uvicorn's loop runs.
  2. Start a daemon thread that reads `sys.stdin.buffer` until EOF and then sets `server.should_exit = True`.
  3. Run `uvicorn.Server(uvicorn.Config(app.main.app, workers=1, log_config=…)).run(sockets=[sock])`.

  Startup ordering (`desktop_main(stdin, stdout, server_factory)`) is a small function, so tests can drive it with fakes. Docker and `mac-worker` keep their current commands and never import this module. At startup it logs the names (never the values) of the desktop settings it received.
- **`app/main.py`:**
  - Add `ApiToken`, an ASGI middleware written like the existing `RequestSizeLimit`. It is registered in `create_app()` only when `TTS_API_TOKEN` is non-empty, compares with `hmac.compare_digest`, and returns `JSONResponse(401, {"detail": "Unauthorized"})`.
  - `lifespan` passes `model_dir=Path(os.environ["TTS_MODEL_DIR"])`, when set, to `ChatterboxEngine`.
  - Device selection is unchanged: the existing `select_device` already handles `auto`, `cpu` and `mps`. The device is not part of the batch checkpoint identity, and conditioning loads with `map_location`, so either device works with the same data.
- **`app/engine.py`:**
  - `ChatterboxEngine.__init__` gains `model_dir: Path | None = None`.
  - In `load()`, the `multilingual-v3` branch uses `checkpoint_dir = verify_bundled_model(model_dir, model_revision)` instead of `snapshot_download` when `model_dir` is set.
  - `verify_bundled_model` is new and pure. It reads `model-manifest.json` (`{"repository","revision","files":{name:{"size","sha256"}}}`) and checks the repository, the revision and every file in `MODEL_FILES` by existence and size. It never hashes 3 GB at startup; SHA-256 is verified once at build time.
  - Failures raise `SynthesisError("The bundled voice model is missing or damaged. Reinstall Perene TTS.")`. The existing `_load_error` path surfaces this through `/health`.
- **`app/studio.py`:** `ffmpeg()` uses `os.getenv("TTS_FFMPEG", "ffmpeg")` as argv[0]. Nothing else changes, so encode settings and validation stay identical.
- **Bundled FFmpeg:** a pinned FFmpeg release built from source on the Mac with `MACOSX_DEPLOYMENT_TARGET=14.0`, `--arch=arm64`, `--disable-autodetect` (so it never picks up AudioToolbox, VideoToolbox or Homebrew libraries), `--disable-everything --disable-gpl --enable-libmp3lame` and a static LAME built with the same deployment target. It enables only:
  - decoders `mp3*`, `pcm_*`
  - demuxers `mp3`, `wav`
  - encoders `libmp3lame`, `pcm_s16le`
  - muxers `mp3`, `wav`
  - parser `mpegaudio`
  - filters `aresample`, `aformat`, `anull`
  - protocols `file`, `pipe`.

  `otool -L` must list only `/usr/lib` system libraries. The build runs the existing pytest suite with this binary first on `PATH` and `TTS_FFMPEG` set, which proves every command in `studio.py` and the tests works. The result is LGPL-2.1+ with its license texts and a source pointer. **Fallback:** if a needed component is missing, add it to the configure list. Never use a GPL or Homebrew build without recording the license change.

### Desktop host in WebApp
Desktop mode stays inside `WebApp` rather than a new project. There is one composition root and the static web assets pipeline is unchanged (`MapStaticAssets`, `RequiresAspNetWebAssets`). The only new package is `Photino.NET` 4.0.16. Its native library is never loaded in Docker mode.

- **`WebApp/StudioApp.cs` (new):** moves the current `Program.cs` body into two methods. `AddStudio(WebApplicationBuilder, StudioHostOptions)` covers Razor components, the typed `TtsClient` with base address and optional `X-Perene-Token` default header, and the scoped `StudioSession`. `MapStudio(WebApplication)` covers the exception handler, static files, antiforgery, `/health`, `/error`, the three streaming routes and `StreamFromWorker`. Docker mode calls both with today's values (`Tts:BaseUrl` default `http://tts:5081/`), so FR24 holds by construction.
- **`WebApp/Desktop/DesktopLaunch.cs` (new, pure):** `ShouldRun(string[] args, string baseDirectory)` returns true for `--desktop`, or when `baseDirectory` ends in `<name>.app/Contents/MacOS/`. Finder cannot pass arguments, so the bundle path is the trigger.
- **`WebApp/Program.cs`:** `if (DesktopLaunch.ShouldRun(args, AppContext.BaseDirectory)) return DesktopHost.Run(args);`. The rest is the existing three-step build, now through `StudioApp`. The top-level body stays free of `await`, so the entry point keeps running on the process main thread. AppKit requires that.
- **`WebApp/Desktop/DesktopHost.cs`:** runs synchronously on the main thread, because Photino/AppKit needs the creating thread for the run loop. Steps:
  1. Resolve `DesktopPaths`.
  2. Acquire `InstanceLock`. If that fails, show the native alert "Perene TTS is already running." and exit with code 0.
  3. `WorkerProcess.StartAsync().GetAwaiter().GetResult()`.
  4. Build the `WebApplication`:
     - `ContentRootPath = AppContext.BaseDirectory` (`Contents/MacOS/`, where `wwwroot` and the static web assets manifest live)
     - `WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0))`
     - `AllowedHosts=127.0.0.1`
     - `AddDataProtection().SetApplicationName("perene-tts").PersistKeysToFileSystem(paths.Keys)`
     - `AddStudio(...)` with the worker URL and token
     - register `PhotinoHostShell` as `IHostShell`
     - add `DesktopAccessMiddleware` before `MapStudio`.
  5. `app.StartAsync()`, then read `IServerAddressesFeature`.
  6. Create the `PhotinoWindow`: title "Perene TTS", size 1280×800, centered, `RegisterWindowClosingHandler(closeCoordinator.OnClosing)`, `Load(<url>/?launch=<token>)`, `WaitForClose()`. The Dock and ⌘-Tab icon comes from the bundle's `AppIcon.icns`, not from Photino.
  7. After the window closes: `app.StopAsync()`, then `WorkerProcess.StopAsync(10 s)`, then release the lock.
- **Native alerts before a window exists:** use Photino's message box if Phase 1a shows it works without a window. Otherwise `NativeAlert` runs `/usr/bin/osascript -e 'display alert …'` with the text passed as an argument, never interpolated into the script.
- **`WebApp/Desktop/DesktopAccessMiddleware.cs`:**
  - `GET /?launch=<token>` with the right token (constant-time compare) sets cookie `perene-launch` (HttpOnly, SameSite=Strict, Path=/, session) and redirects to `/`.
  - Any request without a valid cookie gets `403`.
  - The cookie value is a per-launch random 32-byte base64url string, separate from the worker token.
- **`WebApp/Desktop/WorkerProcess.cs`:**
  - Builds `ProcessStartInfo` (no shell; env as listed above; `RedirectStandardInput/Output/Error`).
  - Reads stdout until `WorkerReadyLine.TryParse` succeeds, with a 120 s timeout. After that, it pumps stdout and stderr into `worker.log`, prefixed with a UTC timestamp. The previous log is rotated to `worker.previous.log`.
  - The `Exited` event sets `WorkerState.Stopped`, which `StudioSession` shows as FR10's message.
  - `StopAsync` closes stdin, waits up to 10 s, then calls `Kill(entireProcessTree: true)`.

### Shell abstraction and UI changes
`WebApp/Services/IHostShell.cs` defines:
- `bool IsDesktop`
- `Task<string?> PickSaveFileAsync(string suggestedName, FileFilter filter)`
- `Task<IReadOnlyList<string>> PickFilesAsync(FileFilter filter, bool multiple)`
- `Task<string?> PickFolderAsync()`
- `event Func<CloseRequest, Task>? CloseRequested`
- `void CompleteClose()`.

There are two implementations:
- `BrowserHostShell` (web mode): `IsDesktop = false`; the pickers throw `NotSupportedException` and are never called.
- `PhotinoHostShell` (desktop): it calls Photino's `ShowSaveFileAsync`, `ShowOpenFileAsync` and `ShowOpenFolderAsync` through `window.Invoke` and a `TaskCompletionSource`, so the panels run on the main thread. The app is not sandboxed, so files and folders the user picks are readable without extra permission prompts.

Components depend only on `IHostShell`.

- **Downloads:** add `WebApp/Components/DownloadButton.razor`. It takes `Href`, `SuggestedName`, `Label`, `Icon` and `CssClass`.
  - In web mode it renders today's `<a ... download>` markup.
  - In desktop mode it renders a `<button>` that calls `AudioExport.SaveAsync(href, suggestedName)`.

  Replace the four `<a download>` uses: `GenerationOutput.razor` line 14, `Creations.razor` line 65, and `BatchRow.razor` lines 48 and 85. Suggested names:
  - `perene-{id}.mp3`
  - `BatchRules.OutputName(batchName, number)` for tracks (and for batch creations in `Creations.razor`)
  - `<Batch name>.zip` (new `BatchRules.ArchiveName`).
- **`WebApp/Services/AudioExport.cs`:** maps the href to the matching `TtsClient.Open*Async` call. It parses only the three known route shapes and rejects anything else. It asks `IHostShell` for a path, starting in `~/Music` (from `DesktopPaths`, else home), streams to `<path>.part`, then calls `File.Move(..., overwrite: true)`. Cancel is a no-op. Errors delete `.part` and set `StudioSession.Error`. Success sets the new `StudioSession.Notice` ("Saved <file name>").
- **Notices:** `MainLayout.razor` renders `Studio.Notice` in a `<div class="notice" role="status" aria-live="polite">` with a Dismiss button, next to the existing `.alert`. It auto-clears after 6 s. The `.notice` style goes in `app.css` and uses the existing tokens (`--bg-elevated`, `--brand-green`, `--border`) in both themes.
- **Pickers:**
  - `BatchAudio.razor`: in desktop mode, "Choose folder" and "Choose files" are `<button class="button secondary">` elements calling `IHostShell`. Their visible labels, icons and focus order match today's picker labels.
  - `Home.razor`: same treatment for the reference recording.
  - Selected paths become `LocalSourceFile` items (name = file name, size from `FileInfo`, `Open()` = `File.OpenRead` wrapped in `BoundedReadStream(maxBytes)`, which throws `IOException` past the limit). The size is checked again at read time.
  - Browser picks keep `IBrowserFile`. Both kinds implement `ISourceFile { string Name; long Size; Stream Open(long maxBytes, CancellationToken) }`, so `ChooseFiles`, `BatchRules.DefaultOrder`, `MeasureTextAsync` and `StartBatch` share one path. `LazyBrowserFileStream` generalizes to `LazyOpenStream(Func<Stream>, long length)`.
  - Folder enumeration (`LocalSourceFiles.Enumerate`) is recursive, skips symlinked directories and `.DS_Store`/AppleDouble (`._*`) files, and caps at `BatchRules.MaxFiles + 1`, mirroring `webkitdirectory`.
  - Only names and contents are uploaded, never paths, which preserves the AGENTS.md constraint.
- **Empty, loading and error states:** while a native dialog is open, the triggering button is disabled ("Choosing…"). An empty or cancelled selection leaves the current state untouched. Read errors reuse the existing per-file messages. Responsive layout and theming are unchanged because the same CSS classes are reused.

### Closing the window
- **Photino side:** `CloseCoordinator.OnClosing` is the Photino `NetClosingDelegate`. Returning `true` cancels the close.
  - If `allowClose` is set, it returns `false`.
  - Otherwise it returns `true` and dispatches `RequestAsync()` to the thread pool.
- **`RequestAsync()`:**
  1. Raise `IHostShell.CloseRequested`. `MainLayout` subscribes per circuit.
  2. If no circuit acknowledges within 2 s (page not loaded or crashed), fall back to the host: fetch `TtsClient.BatchesAsync` and show a native Yes/No message with the same wording.
- **In the circuit:** `MainLayout` awaits `Studio.Refresh()` and evaluates `DesktopRules.CloseDecision(Studio.Busy, Studio.Batches)`. It returns `CloseNow`, `ConfirmStopBatches(blocking batches)` or `ConfirmDiscardGeneration`, where blocking means state `queued`, `running` or `pausing`.
  - `CloseNow` calls `shell.CompleteClose()` immediately.
  - Otherwise a `ConfirmDialog` opens: Danger, Confirm label "Stop conversion and quit" or "Discard and quit", Cancel label "Keep working". The body lists the batch names and says "Completed tracks are kept. Use Retry later to convert the rest."
  - On confirm, call `Studio.StopBatch(id)` for each blocking batch, re-check that none remain in `queued`/`running`/`pausing`, then call `CompleteClose()`.
  - If a stop fails, show the existing error and keep the window open.
- **`CompleteClose()`** sets `allowClose` and calls `window.Invoke(window.Close)`. Closing the only window quits the app, so no Perene process stays running without a window.
- **⌘Q and Dock → Quit:** Phase 1a records whether these reach `OnClosing`. If they do, they follow the rules above. If not, the approved fallback applies: the process exits, the kernel closes the worker's stdin pipe, the worker shuts down gracefully (FR3), and on the next launch `BatchService.recover()` re-queues interrupted batches.
- **Safety at shutdown:** `stopping` is safe. The runner observes it at the chunk boundary, and if the worker shuts down first, `BatchService.recover()` finalizes it as `stopped` on next launch (`batch_service.py` lines 188–201).
- **Logout, restart or power off:** confirmation is impossible. The .NET host stops normally, the worker gets EOF, and on the next launch the existing recovery re-queues interrupted batches. This is documented in the README.

### Directories, logs, and single instance
- `DesktopPaths.Resolve(string home, string baseDirectory, IDictionary env)` is pure. It returns:
  - Data `~/Library/Application Support/Perene TTS`, with `studio`, `keys` and `instance.lock` under it
  - Logs `~/Library/Logs/Perene TTS`
  - Cache `~/Library/Caches/Perene TTS` (used for `HF_HOME` and `NUMBA_CACHE_DIR`)
  - Music `~/Music` when it exists, else home
  - Resources: when `baseDirectory` is inside a bundle, `Contents/Resources` (model, licenses) and `Contents/MacOS` (worker, `ffmpeg`). For unbundled development runs (`dotnet run -- --desktop`), the `PERENE_RESOURCES` environment variable points at an unpacked layout.

  Directories are created with `UnixFileMode` `0700`. The structure leaves room for a later Linux (XDG) branch.
- `InstanceLock` opens `instance.lock` with `FileShare.None` and keeps the stream open for the process lifetime. On macOS, .NET implements this with an advisory `flock`, which the kernel releases on exit. Launch Services already brings a running app to the front on a second double-click, so the lock mainly covers Terminal launches.
- In the app, `StudioSession.HealthText` and `Error` keep their existing wording ("Voice engine ready · MPS" or "· CPU" already comes from `/health`). Only FR10's "stopped unexpectedly" message is new. It is fed by an optional injected `IWorkerStatus`, which web mode does not register.

### Packaging the .app and DMG
`packaging/macos/build.sh` is a Bash script called by `make desktop-mac`. It works under `build/macos/` (ignored), keeps a download cache there between runs, and writes the results to `dist/`.

| Step | Output |
| --- | --- |
| `preflight` | Requires `Darwin` + `arm64`, `dotnet --version` 10.x, python.org Python 3.11 (`/Library/Frameworks/Python.framework/Versions/3.11`), `xcode-select -p`, and about 25 GB free disk. Names anything missing and stops. |
| `model` | Build venv runs `snapshot_download(ResembleAI/chatterbox, MODEL_REVISION, MODEL_FILES)` into the cache → `model/` plus `model-manifest.json` with size and SHA-256 per file, then re-hashes every file |
| `ffmpeg` | Pinned FFmpeg and LAME source tarballs (SHA-256 checked) → minimal LGPL `ffmpeg` for arm64, deployment target 14.0, plus licenses |
| `worker` | python.org 3.11 venv in `build/macos/venv`. Installs `requirements.lock.txt` and then `--no-deps requirements.txt`, as `scripts/mac-worker.sh setup` does (PyPI's macOS arm64 torch 2.6.0 wheel includes MPS). Adds pinned PyInstaller and pytest from `packaging/macos/requirements-build.txt`. Runs pytest against the bundled FFmpeg, then `pyinstaller packaging/macos/worker.spec` → `perene-worker` (`onedir`). |
| `webapp` | `dotnet publish WebApp/WebApp.csproj -c Release -r osx-arm64 --self-contained -o build/macos/app` (folder publish, not single-file) |
| `bundle` | Assemble `Perene TTS.app` (layout below), write `Info.plist` from `packaging/macos/Info.plist.in` and `VERSION`, build `AppIcon.icns` |
| `check` | For every Mach-O file: `lipo -archs` includes arm64, `vtool -show-build` minimum OS ≤ 14.0, and `otool -L` lists only `@rpath`/`@loader_path`/`@executable_path` or `/usr/lib`/`/System` libraries. `plutil -lint Info.plist`. |
| `sign` | Ad-hoc sign every Mach-O file inside-out, then the bundle (see below). `codesign --verify --deep --strict --verbose=2`. |
| `dmg` | Stage the app, an `Applications` symlink and `How to open.txt`. `hdiutil create -volname "Perene TTS" -srcfolder <stage> -format ULFO` → `dist/Perene-TTS-macos-arm64.dmg`. Print `du -sh` per component, the app and the DMG. |

**Bundle layout:**
```
Perene TTS.app/Contents/
├── Info.plist
├── MacOS/
│   ├── WebApp                  CFBundleExecutable (.NET apphost, self-contained osx-arm64)
│   ├── *.dll, *.dylib, libPhotino.Native.dylib, wwwroot/, appsettings.json, *.staticwebassets.endpoints.json
│   ├── worker/perene-worker    PyInstaller onedir (+ _internal/)
│   └── ffmpeg                  minimal LGPL FFmpeg
└── Resources/
    ├── AppIcon.icns
    ├── model/                  weights + model-manifest.json
    └── licenses/               THIRD-PARTY-NOTICES.md + license texts
```
Weights stay uncompressed in `Resources`, so model loading reads plain files from the SSD. If Phase 1b shows `codesign` rejects the worker's non-code files under `Contents/MacOS`, the worker moves to `Contents/Resources/worker` and `DesktopPaths` follows.

- **`worker.spec`:**
  - entry `services/ChatterboxTtsService/app/desktop.py`
  - `collect_all` for `chatterbox`, `perth`, `s3tokenizer`, `librosa`, `pykakasi` and `spacy_pkuseg` (refined in Phase 1b)
  - `excludes` for `gradio`, `gradio_client`, `pytest`, `pre_commit`, `virtualenv`, `nodeenv`, `identify`, `cfgv` and `IPython`, which trims dev-only packages without a second lock file
  - `target_arch="arm64"`, `noarchive=False`, no UPX, and no PyInstaller `BUNDLE` step (the script assembles the app).
- **Python source:** the python.org installer is required because its binaries target old macOS versions. Homebrew's Python is built for the Mac it was installed on, so friends on an older macOS could not run the app. The `check` step enforces this.
- **Makefile targets:**
  - `desktop-mac`: `bash packaging/macos/build.sh` (Darwin only).
  - `desktop-smoke`: runs `packaging/macos/smoke.py` (stdlib only, with the system `/usr/bin/python3`) against `dist/Perene TTS.app`, launching the bundled worker under `sandbox-exec` with a profile that denies outbound network except loopback. If `sandbox-exec` is unavailable, the target stops and explains how to rerun with Wi-Fi off and `SMOKE_NO_SANDBOX=1`.
  - `desktop-run`: `open "dist/Perene TTS.app"`.
  - `desktop-icon`: renders `AppIcon-1024.png` from the SVG in a Docker container with `rsvg-convert` (any machine).
- `build/` and `dist/` are added to `.gitignore` and `.dockerignore`.

### Signing and sharing without a paid account
- **Why ad-hoc is enough:** Apple silicon refuses to run arm64 code with no signature at all. An ad-hoc signature proves the files are intact, not who made them. A build made on this Mac carries no quarantine attribute, so Gatekeeper never checks it.
- **Order:** PyInstaller and the .NET SDK already ad-hoc sign some binaries, but files copied into the bundle must be re-covered. The script signs every Mach-O file found under `Contents/` (deepest paths first) with `codesign --force --sign - --timestamp=none`, then signs the bundle itself without `--deep`. It does not pass `--options runtime`, so the hardened runtime is off and neither .NET (`allow-jit`) nor Python needs entitlements.
- **Friends:** AirDrop, browsers and Mail add `com.apple.quarantine` to the DMG, and the copied app inherits it. On first open, macOS says it "could not verify" the app. On macOS 15 and 26, Control-click → Open no longer bypasses this; the friend uses System Settings → Privacy & Security → **Open Anyway** once. `How to open.txt` and the README also give the Terminal alternative, `xattr -dr com.apple.quarantine "/Applications/Perene TTS.app"`. "Damaged and can't be opened" means the signature is broken, which the `check` and `sign` steps prevent.
- **App Translocation:** a quarantined app opened from the DMG or Downloads runs from a random read-only path. The app never writes into its bundle, so it still works, but the instructions say to drag it to Applications first.
- **What is given up:** no notarization, so friends see the warning once. The ad-hoc signature changes on every rebuild, so macOS may treat a rebuilt app as new. The app uses no camera, microphone, contacts or other protected resources, so there are no permission prompts to repeat.

### Icon
`packaging/macos/perene-tts.svg`:
- a 1024×1024 canvas, transparent
- a rounded rectangle on the macOS app-icon grid: `<rect x="100" y="100" width="824" height="824" rx="185" fill="#1B2A22"/>`
- the Bootstrap Icons 1.13.1 `bi-soundwave` path (`M8.5 2a.5.5 0 0 1 .5.5v11…`), scaled from its 16×16 viewBox to about 520 px and centered, with `fill="#D8AC5C"` (Ochre Gold 300).

The colours are proposed in Open Questions. `make desktop-icon` renders `AppIcon-1024.png`, which is committed. The Mac build makes the 16–512 px sizes and their @2x variants with `sips -z` into `AppIcon.iconset`, then runs `iconutil -c icns`. Phase 4 checks how macOS 26 shows the icon in the Dock and Finder. The Bootstrap Icons MIT license is already bundled in `wwwroot/vendor/`.

### Design principles applied
- **Single responsibility:** `DesktopLaunch` (whether), `DesktopPaths` (where), `InstanceLock` (one instance), `WorkerProcess` (sidecar lifetime), `DesktopAccessMiddleware` (who may call), `PhotinoHostShell` (native UI), `NativeAlert` (messages without a window), `CloseCoordinator` + `DesktopRules` (when to close), `AudioExport` (save), and `LocalSourceFiles` (read user-picked files). Each has one reason to change.
- **Dependency inversion:** Razor components depend on `IHostShell`, `ISourceFile` and `StudioSession`, never on Photino. Worker settings are plain environment variables read at the existing seams (`create_app`, `ChatterboxEngine`, `ffmpeg()`, `select_device`).
- **Open/closed:** the Docker path uses the same `StudioApp` composition with its previous values. The worker's new behaviour turns on only through environment variables Docker and `mac-worker` never set.
- **Testable:** the pure functions (`DesktopLaunch`, `WorkerReadyLine`, `DesktopPaths.Resolve`, `DesktopRules`, `verify_bundled_model`, the token middleware) and the I/O services that take fakes (`AudioExport` with a fake handler and a fake shell, `LocalSourceFiles` with temp directories) are unit-tested on any machine. Photino, signing and the bundle are verified on the Mac.

## Component Breakdown

**Existing files to modify:**
- `WebApp/Program.cs`: branch to `DesktopHost.Run` when `DesktopLaunch.ShouldRun`; otherwise build through `StudioApp`.
- `WebApp/WebApp.csproj`: add the `Photino.NET` 4.0.16 package reference.
- `WebApp/Services/StudioSession.cs`: add `Notice` and `SetNotice`, plus an optional `IWorkerStatus` for FR10's message.
- `WebApp/Services/LazyBrowserFileStream.cs`: generalize to `LazyOpenStream(Func<Stream>, long)` (rename the file accordingly).
- `WebApp/Models/BatchRules.cs`: add `ArchiveName`.
- `WebApp/Components/Layout/MainLayout.razor`: notice area, `CloseRequested` subscription and close `ConfirmDialog`.
- `WebApp/Components/GenerationOutput.razor`, `WebApp/Components/Pages/Creations.razor`, `WebApp/Components/BatchRow.razor`: use `DownloadButton`.
- `WebApp/Components/Pages/BatchAudio.razor`: desktop pickers and the `ISourceFile`-based selection model.
- `WebApp/Components/Pages/Home.razor`: desktop reference picker.
- `WebApp/wwwroot/app.css`: `.notice` style and the disabled "Choosing…" picker state.
- `services/ChatterboxTtsService/app/main.py`: `ApiToken` middleware and `TTS_MODEL_DIR` wiring.
- `services/ChatterboxTtsService/app/engine.py`: `model_dir` and `verify_bundled_model`.
- `services/ChatterboxTtsService/app/studio.py`: `TTS_FFMPEG`.
- `Makefile`: `desktop-mac`, `desktop-smoke`, `desktop-run`, `desktop-icon` and help text.
- `.gitignore`, `.dockerignore`: `build/`, `dist/`.
- `README.md`: "Desktop app (macOS)" section. `AGENTS.md` is refreshed through `init-agent` after implementation.

**New files to create:**
- `WebApp/StudioApp.cs`: shared service and endpoint composition.
- `WebApp/Desktop/DesktopLaunch.cs`, `DesktopHost.cs`, `DesktopPaths.cs`, `InstanceLock.cs`, `WorkerProcess.cs`, `WorkerReadyLine.cs`, `DesktopAccessMiddleware.cs`, `PhotinoHostShell.cs`, `NativeAlert.cs`, `CloseCoordinator.cs`.
- `WebApp/Services/IHostShell.cs` (with `BrowserHostShell`), `AudioExport.cs`, `ISourceFile.cs` (with `BrowserSourceFile`, `LocalSourceFile`, `LocalSourceFiles`, `BoundedReadStream`).
- `WebApp/Models/DesktopRules.cs`: pure close decision.
- `WebApp/Components/DownloadButton.razor`.
- `WebApp.Tests/DesktopLaunchTests.cs`, `DesktopRulesTests.cs`, `DesktopPathsTests.cs`, `WorkerReadyLineTests.cs`, `AudioExportTests.cs`, `LocalSourceFilesTests.cs`, `DesktopAccessMiddlewareTests.cs`.
- `services/ChatterboxTtsService/app/desktop.py`.
- `services/ChatterboxTtsService/tests/test_desktop.py`: token middleware, stdin EOF shutdown, ready line, `verify_bundled_model`, `TTS_FFMPEG`.
- `packaging/macos/build.sh`, `worker.spec`, `requirements-build.txt`, `Info.plist.in`, `VERSION`, `How to open.txt`, `smoke.py`, `smoke.sb` (sandbox profile), `perene-tts.svg`, `AppIcon-1024.png`, `THIRD-PARTY-NOTICES.md`.
- `packaging/macos/spike/` (Phase 1 only; deleted before Phase 4 is merged).

## Dependencies
- **Build host: this Mac.** Apple silicon, macOS 14 or later (this one runs 26.6.2). It needs:
  - the .NET 10 SDK for macOS Arm64 (not yet installed): the installer from dotnet.microsoft.com or `brew install --cask dotnet-sdk`
  - Python 3.11 from the python.org macOS installer (not Homebrew; see Packaging)
  - the Xcode Command Line Tools (installed: `codesign`, `lipo`, `vtool`, `otool`, `sips`, `iconutil`, `hdiutil`, `plutil`).

  Internet is needed **at build time only**: about 3 GB of model weights, PyPI wheels, NuGet, and the FFmpeg/LAME source tarballs. Expect about 25 GB of free disk for the cache, venv and outputs. Homebrew's FFmpeg, used by `make mac-setup`, stays a development tool and is never bundled.
- **Any machine:** Docker for `make test` and `make desktop-icon`, as today.
- **Runtime (the user's and friends' Macs):** Apple silicon, macOS 14 or later, and nothing installed. WKWebView ships with macOS.
- **New packages:**
  - `Photino.NET` 4.0.16 (Apache-2.0; targets net8.0/net9.0 and is consumed by net10.0) with `Photino.Native` ≥ 4.0.22.
  - PyInstaller, pinned to the newest 6.x release at implementation time in `packaging/macos/requirements-build.txt` (build only, never in the Docker worker image).
- **Environment variables (worker, desktop only):** `TTS_API_TOKEN`, `TTS_MODEL_DIR`, `TTS_FFMPEG`. None is set in `docker-compose.yml` or `scripts/mac-worker.sh`.

## External / Vendor Documentation Evidence
**Microsoft Learn (via the MCP server):**
- [Install .NET on macOS: supported versions](https://learn.microsoft.com/dotnet/core/install/macos): .NET 10 is supported on macOS 26 "Tahoe", 15 "Sequoia" and 14 "Sonoma". Hence `LSMinimumSystemVersion` 14.0 and the deployment target 14.0 for FFmpeg and LAME. The SDK download offers Arm64 for Apple processors.
- [Publish .NET apps for macOS](https://learn.microsoft.com/dotnet/core/deploying/macos): .NET produces a native apphost that must be signed. Learn lists `com.apple.security.cs.allow-jit` as required for non-Native-AOT apps when signing and notarizing, which applies with the hardened runtime. **Reconciled:** this build does not enable the hardened runtime or notarize, so no entitlements are used. If the hardened runtime is ever enabled, `allow-jit` is needed (plus Python's own entitlements). Learn's universal-binary section confirms per-architecture publishing; only `osx-arm64` is built.
- [Configure endpoints for Kestrel: dynamic port binding (.NET 10)](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0#configure-endpoints): port `0` binds a free port, read back through `IServerAddressesFeature`. `ListenLocalhost` does not support port 0, so the plan uses `Listen(IPAddress.Loopback, 0)`.
- [Web server implementations: IServerAddressesFeature](https://learn.microsoft.com/aspnet/core/fundamentals/servers/?view=aspnetcore-10.0): Kestrel exposes the bound addresses through this feature.
- [Host filtering with Kestrel](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/host-filtering?view=aspnetcore-10.0) and [Kestrel security considerations](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/security-considerations?view=aspnetcore-10.0#security-middleware): `AllowedHosts` (default `*`) rejects unexpected `Host` headers with 400 and prevents DNS rebinding. It is set to `127.0.0.1` in desktop mode.
- [dotnet publish options](https://learn.microsoft.com/dotnet/core/tools/dotnet-publish#options) and [Publishing overview: single-file](https://learn.microsoft.com/dotnet/core/deploying/#publish-as-single-file): `--self-contained` with a RID bundles the runtime. A folder publish is used inside the app bundle, avoiding single-file extraction cost.
- [Key storage format](https://learn.microsoft.com/aspnet/core/security/data-protection/implementation/key-storage-format?view=aspnetcore-10.0) and [key storage providers](https://learn.microsoft.com/aspnet/core/security/data-protection/implementation/key-storage-providers?view=aspnetcore-10.0): on macOS the default key ring is `$HOME/.aspnet/DataProtection-Keys`. `PersistKeysToFileSystem` relocates it to Application Support. Learn advises restricting the directory's permissions (hence `0700`) and notes keys are then not encrypted at rest, which is acceptable for local antiforgery and circuit data. The [configuration overview](https://learn.microsoft.com/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0) notes the application discriminator depends on the app path. Translocation can change that path, so `SetApplicationName("perene-tts")` is set.
- [Blazor Hybrid render modes](https://learn.microsoft.com/aspnet/core/blazor/hybrid/tutorials/maui-blazor-web-app?view=aspnetcore-10.0#use-blazor-render-modes) and [Blazor file downloads](https://learn.microsoft.com/aspnet/core/blazor/file-downloads?view=aspnetcore-10.0):
  - BlazorWebView runs components with no render mode, which would require changing `App.razor`, the streaming endpoints and the asset pipeline. **Repo constraint kept:** Interactive Server stays, served by in-process Kestrel.
  - Learn's download guidance assumes a browser's download manager. An embedded WKWebView has none wired up by Photino, so desktop downloads use native save dialogs instead. **Documented difference.**

**Non-Microsoft sources:**
- [Photino.NET on this Mac — build target & unsigned app guide](https://claude.ai/code/artifact/b25d4ee0-3d2d-4a4f-8f5f-9451c0f7a7eb) (2026-10-12): the machine profile, `osx-arm64` on .NET 10, the minimal `.app` layout and `Info.plist`, ad-hoc signing, no hardened runtime, and the Gatekeeper fixes (Open Anyway; `xattr` removal).
- Photino.NET [`PhotinoWindow.NET.cs`](https://github.com/tryphotino/photino.NET/blob/master/Photino.NET/PhotinoWindow.NET.cs) and [`PhotinoNetDelegates.cs`](https://github.com/tryphotino/photino.NET/blob/master/Photino.NET/PhotinoNetDelegates.cs) provide:
  - `ShowSaveFileAsync`, `ShowOpenFileAsync(multiSelect, filters)`, `ShowOpenFolderAsync`, `ShowMessage`, `Invoke`, `Load(Uri)`, `WaitForClose`, `Close`
  - `NetClosingDelegate(object, EventArgs) → bool`, where "return true to prevent the window from closing".
  - On macOS, Photino uses the system WKWebView. **Pending verification in Phase 1a:** ⌘Q routing, alerts without a window, and ATS for loopback HTTP.
- [NuGet Photino.NET](https://www.nuget.org/packages/Photino.NET): latest 4.0.16 (Jan 2025), targets net8.0/net9.0, depends on Photino.Native ≥ 4.0.22. Upstream activity has been low since then, which is recorded as a risk.
- [Gatekeeper and quarantine on macOS Tahoe](https://macos-tahoe.com/blog/app-is-damaged-cant-be-opened-macos-tahoe-gatekeeper-quarantine-fix-2026/) (third-party): the Open Anyway location and quarantine removal.

## Flow
```mermaid
sequenceDiagram
    participant U as User
    participant H as WebApp (desktop mode)
    participant W as perene-worker (app/desktop.py)
    participant K as Kestrel 127.0.0.1:0 (StudioApp)
    participant P as PhotinoWindow (WKWebView)
    U->>H: double-click Perene TTS.app
    H->>H: DesktopLaunch.ShouldRun (bundle path), DesktopPaths.Resolve, InstanceLock.Acquire
    H->>W: spawn (env: token, data, model, ffmpeg, device, offline)
    W-->>H: stdout "PERENE_WORKER_READY port=N"
    W->>W: lifespan: recover(), runner.start(), load model (background)
    H->>K: StartAsync (TtsClient → 127.0.0.1:N + X-Perene-Token)
    K-->>H: IServerAddressesFeature → http://127.0.0.1:M
    H->>P: Load http://127.0.0.1:M/?launch=T
    P->>K: GET /?launch=T → Set-Cookie perene-launch, 302 /
    P->>K: Blazor circuit (StudioSession polls /health, /batches)
    K->>W: GET /health (X-Perene-Token)
    W-->>K: model_ready=false → true (device mps or cpu)
    U->>P: Download MP3
    P->>K: DownloadButton click (circuit)
    K->>P: PhotinoHostShell.ShowSaveFileAsync
    K->>W: GET /audio/{id} → stream to path.part → rename
    U->>P: close window (batch running)
    P->>H: NetClosingDelegate → true (cancel)
    H->>K: CloseRequested → MainLayout ConfirmDialog
    U->>P: "Stop conversion and quit"
    K->>W: POST /batches/{id}/stop → stopping
    K->>H: CompleteClose → window.Close
    H->>K: StopAsync
    H->>W: close stdin → graceful shutdown (≤10 s, then kill tree)
```

## Risk Assessment

| Risk | Evidence | Mitigation |
| --- | --- | --- |
| ⌘Q or Dock → Quit bypasses the closing handler, so a running batch is not confirmed | macOS treats app termination separately from window close; Photino's handler is documented for the window | Phase 1a records the behaviour. Fallback: stdin-EOF shutdown and `recover()` re-queue the batch on next launch. Needs user approval (Open Questions). |
| `codesign` fails on the PyInstaller/torch bundle, or friends see "damaged" | Thousands of `.so`/`.dylib` files; non-code files under `Contents/MacOS` | Inside-out signing, then the bundle without `--deep`. `codesign --verify --deep --strict` in the build. Phase 1b fallback moves the worker to `Contents/Resources`. |
| Worker or FFmpeg blocked on a friend's Mac even after Open Anyway | Quarantine is inherited by every file copied out of the DMG | Phase 1b quarantine test (ideally on a friend's Mac). Fallback: the README requires `xattr -dr com.apple.quarantine`, with user approval. |
| A bundled binary needs a newer macOS than a friend has | Homebrew Python/FFmpeg target the build Mac's OS | python.org Python, deployment target 14.0, and the `check` step fails the build on any Mach-O with a newer minimum or an external library path. |
| WKWebView refuses loopback HTTP or the launch cookie | App Transport Security; WebKit cookie rules for IP hosts | `NSAllowsLocalNetworking` in `Info.plist`. Phase 1a proves load, cookie and circuit. |
| MPS fails or is slower for some operations | Not all PyTorch ops run on MPS; AGENTS.md warns against assuming MPS is faster | `PYTORCH_ENABLE_MPS_FALLBACK=1`; Phase 1b measures CPU vs MPS; ship `cpu` if MPS fails or is slower. |
| PyInstaller misses dynamic imports or data (perth watermark weights, librosa lazy loaders, transformers) | Chatterbox uses package data and lazy imports | `collect_all` list in `worker.spec`. The network-denied smoke test exercises voice and speech, and manual tests cover batch. Phase 1b iterates until it passes. |
| A hidden network call at runtime | `snapshot_download` today; HF libraries may probe | `TTS_MODEL_DIR` path skips the download. `HF_HUB_OFFLINE=1` and `TRANSFORMERS_OFFLINE=1`. The smoke test runs under a deny-network sandbox profile, and manual verification uses Wi-Fi off. |
| Large app and DMG to share | 3.0 GB weights, about 1 GB torch, more for libraries | Sizes recorded (FR25). DMG compressed with `ULFO`. The size budget is an Open Question. |
| Minimal FFmpeg build is missing a component that a real file needs | `studio.py` decodes arbitrary user MP3/WAV | Configure list covers every MP3/WAV path in use. The pytest suite (real FFmpeg) runs against the bundled binary in the build. Manual test with real MP3 and WAV references. |
| Other local users or processes reach the loopback servers | Loopback ports are per machine, not per user | Worker token, web launch cookie, `AllowedHosts=127.0.0.1`, random ports, `0700` data directories. |
| Library injection without the hardened runtime | Ad-hoc, non-hardened apps honour `DYLD_*` variables | Accepted for a personal, hand-shared build and documented in the README. Revisit if the app is ever distributed widely. |
| Closing logic leaves a worker orphan or loses work | Process kill, crash, logout | stdin-EOF shutdown also fires when the parent dies, then a 10 s tree kill. Recovery semantics already exist (`recover()`). Single-instance lock prevents two workers on one data directory. |
| Docker or `mac-worker` mode regresses through the `Program.cs` refactor | Shared composition moves into `StudioApp` | Same values in web mode. `make test` compiles the runtime stage. Manual `make docker-run-bg` smoke in Validation. |
| Photino/AppKit thread affinity | AppKit requires the main thread | `DesktopHost.Run` is synchronous on the main thread, with no `await` before it. Dialogs and close run through `window.Invoke`. |
| Photino upstream is quiet | NuGet's last release is Jan 2025 | Pin 4.0.16. All Photino calls sit behind `IHostShell`, `CloseCoordinator`, `NativeAlert` and `DesktopHost`, so a fork swap is local. |
| License obligations when sharing with friends | FFmpeg and LAME are LGPL; model weights have their own license | LGPL-only FFmpeg run as a separate executable, with a source pointer. `THIRD-PARTY-NOTICES.md` plus license texts in the app. The model license must be confirmed before sharing. |
| macOS 26 shows the icon oddly | macOS 26 restyles app icons that don't follow its grid | The SVG follows the app-icon grid. Phase 4 checks the Dock, Finder and ⌘-Tab. |
