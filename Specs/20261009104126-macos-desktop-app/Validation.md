# Validation: macOS Desktop App (Offline, Ad-hoc Signed .app)

## Table of Contents
- [Acceptance Criteria](#acceptance-criteria)
- [Test Cases](#test-cases)
- [Manual Verification](#manual-verification)
- [Measurements](#measurements)
- [Definition of Done](#definition-of-done)
- [Rollback Plan](#rollback-plan)

## Acceptance Criteria

| Requirement | Acceptance Criterion |
| --- | --- |
| FR1 | Running the bundled worker (`Contents/Resources/python/bin/python3.11 -I -m app.desktop`) prints exactly one first line matching `^PERENE_WORKER_READY port=\d+$`. `lsof -nP -iTCP -sTCP:LISTEN` shows it listening only on `127.0.0.1:<port>`. `/health` answers on that port. One Python process serves it. |
| FR2 | With `TTS_API_TOKEN=abc`, `GET /health` without the header, or with `X-Perene-Token: abd`, returns `401` `{"detail":"Unauthorized"}`. With `abc` it returns `200`. In Docker (`make docker-run-bg`), `curl localhost:5081/health` still returns `200` without a header. |
| FR3 | Closing the worker's stdin makes it log the lifespan shutdown and exit with code 0 within 10 s. Killing the parent .NET process with `kill -9` makes the worker exit on its own within 10 s (no orphan in `ps`). |
| FR4 | With a valid `TTS_MODEL_DIR`, `/health` reaches `model_ready: true` with networking denied, and no `snapshot_download` call occurs (pytest monkeypatch). If a file is deleted, truncated, or the manifest revision is changed, `load_error` reads "The bundled voice model is missing or damaged. Reinstall Perene TTS." |
| FR5 | With `TTS_FFMPEG=/opt/ff/ffmpeg`, `studio.ffmpeg` runs that path (pytest captures argv[0]). The full existing pytest suite passes in the build venv against the bundled minimal FFmpeg. A real 192 kbps MP3 is produced from both an MP3 and a WAV reference. |
| FR6 | `make desktop-smoke` passes with outbound network denied. On the Mac with Wi-Fi off, voice creation, speech, and a 2-file batch all complete. `worker.log` contains no connection errors or retries to `huggingface.co`. |
| FR7 | Double-clicking the app opens a window titled "Perene TTS" showing the studio UI, and the Dock shows the Perene icon. `lsof -nP -iTCP -sTCP:LISTEN` shows the .NET process listening only on `127.0.0.1:<random>`. A request with `Host: evil.example` gets `400`. `make docker-run-bg` still serves http://localhost:8082 as before. |
| FR8 | While the app runs, `curl http://127.0.0.1:<web-port>/` from Terminal gets `403`. A wrong `?launch=` value gets `403`. The window works normally, including the Blazor circuit and audio playback. |
| FR9 | `worker.log` lists the names (no values) of the desktop settings received. `ps -o args= -p <worker pid>` contains no token. Replacing the worker binary with one that never prints the ready line produces, after 120 s, a native alert naming `~/Library/Logs/Perene TTS/worker.log`, and the app exits with no process left behind. |
| FR10 | `kill -9 <worker pid>` while the window is open makes the status show "The voice engine stopped unexpectedly. Close and reopen Perene TTS." within one poll (≤ 5 s). The exit code appears in `worker.log`. |
| FR11 | Double-clicking the app again brings the existing window forward. Running `"/Applications/Perene TTS.app/Contents/MacOS/WebApp"` from Terminal shows "Perene TTS is already running." and exits. `pgrep -f app.desktop` still shows exactly one worker. After `kill -9` of the first instance, a new launch succeeds. |
| FR12 | After first launch, `~/Library/Application Support/Perene TTS/{studio,keys}`, `~/Library/Logs/Perene TTS` and `~/Library/Caches/Perene TTS` exist with mode `drwx------`, and `studio` is empty. Nothing is created in `~/.aspnet`, the launch directory, `~/.cache/huggingface` or the app bundle (`codesign --verify --deep --strict` still passes after use). Replacing the app with a rebuilt one keeps all voices, creations and batches. |
| FR13 | With a running batch, the red close button and ⌘W show the dialog naming the batch. "Keep working" leaves the window and batch unchanged. "Stop conversion and quit" moves the batch to `stopping`/`stopped` and the app quits. With a running speech generation, the "Discard and quit" dialog appears instead. With nothing active, or with a `paused` batch only, the app quits immediately. ⌘Q and Dock → Quit behave the same, or follow the approved fallback recorded in Plan.md. |
| FR14 | After the stop-and-quit above, the worker process is gone within 10 s. On next launch, that batch shows `stopped`, its completed tracks are listed, and Retry converts only the remaining tracks. |
| FR15 | Each of the four download controls opens a native save dialog. Suggested names are `perene-<id>.mp3`, `<Batch> 003.mp3` and `<Batch>.zip`, starting in `~/Music`. The saved files match the worker's bytes (the SHA-256 of a track equals `output_sha256`). Cancel writes nothing. A forced write error (read-only folder) shows an error and leaves no `.part` file. A success notice is announced (`role="status"`). In Docker mode the controls are still `<a download>` links. |
| FR16 | "Choose folder" on a copy of the `in-milton-lumky-territory` folder lists the same 18 files, in the same order, as the browser picker did; `.DS_Store` is ignored. "Choose files" multi-select yields the same list. A 1 MiB + 1 byte `.txt` file shows the existing per-file error. Create a voice accepts an MP3 and a WAV through the native dialog. The worker's multipart requests contain only file names and contents (verified by an xUnit fake handler), with no directory paths. |
| FR17 | On a machine with only Docker (the Mac's Docker Desktop, or Linux), `make desktop-mac` produces `dist/Perene TTS.app` and `dist/Perene-TTS-macos-arm64.zip`, and prints per-component, app and zip sizes. Running `build.sh` outside the builder stops at `preflight` and names the missing tool. No .NET SDK, Python, Homebrew or Xcode tools are installed on the Mac. The app contains the .NET app, the bundled Python with the worker and its packages, model plus manifest, FFmpeg, `Info.plist`, `AppIcon.icns` and licenses. |
| FR18 | The `check` step reports every Mach-O file as arm64 with a minimum macOS ≤ 14.0 and no library paths outside the bundle, `/usr/lib` or `/System`. Adding a Homebrew-linked test binary to the bundle makes the build fail. |
| FR19 | On the Mac, `codesign -dv "dist/Perene TTS.app"` shows `Signature=adhoc` and no `runtime` flag, and `codesign --verify --deep --strict --verbose=2` passes (`make desktop-smoke` runs it). In the builder, `rcodesign print-signature-info` reports `ADHOC` for the main executable, FFmpeg and Python. |
| FR20 | `plutil -lint` passes, and `defaults read ".../Info.plist"` shows every listed key and value. |
| FR21 | `AppIcon-1024.png` shows a gold soundwave on a green rounded square with transparent margins (checked on light and dark backgrounds). `AppIcon.icns` contains the 16–512 px sizes and their @2x variants. Finder, the Dock and ⌘-Tab show the icon on macOS 26. |
| FR22 | Unzipping the zip in Finder gives the app and `How to open.txt`, with Python's symlinks and executable permissions intact. A copy with a quarantine attribute is blocked on first open, opens after Open Anyway, and then reaches "Voice engine ready" (so the worker and FFmpeg subprocesses ran). |
| FR23 | `make desktop-smoke` exits 0 and prints the device, model-ready time, voice job time, speech job time and MP3 size. Breaking `TTS_MODEL_DIR` makes it exit non-zero with a clear message. |
| FR24 | `make docker-check`, `make test` and `make docker-run-bg` succeed. The UI at http://localhost:8082 creates a voice, generates speech and downloads through `<a download>` exactly as before. `docker-compose.yml`, `docker-compose.mac.yml`, the root `Dockerfile` and `scripts/mac-worker.sh` are unchanged. |
| FR25 | README.md has the "Desktop app (macOS)" section (build, install, sharing with friends, Open Anyway and `xattr`, data paths, update, uninstall, logs, limitations including no hardened runtime and quit/logout behaviour). AGENTS.md mentions desktop mode, `packaging/macos/` and the new Make targets. The [Measurements](#measurements) table is filled in. |

## Test Cases

**Unit tests (pytest, `services/ChatterboxTtsService/tests/test_desktop.py`, shared `FakeEngine`, real FFmpeg, no model download):**
- `ApiToken` middleware:
  - no header, wrong header, or a header of different length → `401`
  - correct header → route reached
  - when `create_app()` is built without `TTS_API_TOKEN` (monkeypatch env), the middleware is absent and `TestClient` calls succeed with no header.
- `desktop_main` with fakes:
  - the ready line is written before `server.run` is called and contains the bound port
  - the socket is bound to `127.0.0.1`
  - an EOF on the fake stdin sets `should_exit`.
- `verify_bundled_model(tmp_path, MODEL_REVISION)`:
  - passes for a manifest whose files exist with the recorded sizes
  - raises for a missing file, a size mismatch, a wrong revision, a wrong repository, or a malformed JSON manifest.
- `ChatterboxEngine(model_dir=...)` with `snapshot_download` monkeypatched to raise: `load()` never calls it. A stub `ChatterboxMultilingualTTS.from_local` receives the bundled directory.
- `studio.ffmpeg` with `TTS_FFMPEG` set: the monkeypatched `subprocess.run` sees that executable. Unset, it sees `ffmpeg`.
- The existing suite (`test_studio.py`, `test_batch_*.py`, `test_startup.py`, …) is unchanged and passes.

**Unit tests (xUnit, `WebApp.Tests/`, run by the `web-test` stage):**
- `DesktopLaunchTests`:
  - `--desktop` → true
  - `/Applications/Perene TTS.app/Contents/MacOS/` (with and without a trailing slash, and with spaces) → true
  - `/workspace/WebApp/bin/Debug/net10.0/` and `/app/` (Docker) with no arguments → false.
- `DesktopRulesTests` (Theories):
  - Batches in `queued`/`running`/`pausing` → `ConfirmStopBatches` with exactly those batches.
  - `paused`/`stopping`/`stopped`/`completed`/`failed` → `CloseNow`.
  - `Busy` with no blocking batches → `ConfirmDiscardGeneration`.
  - Both `Busy` and blocking batches → stop-batches wording that also mentions the generation.
- `WorkerReadyLineTests`:
  - accepts `PERENE_WORKER_READY port=41873`
  - rejects ports 0 and 65536, extra text, other prefixes and empty lines.
- `DesktopPathsTests`:
  - a home of `/Users/ana` gives the Application Support, Logs and Caches paths in the table
  - a bundled base directory gives `Contents/Resources` for the model and licenses, and `Contents/MacOS` for the worker and FFmpeg
  - an unbundled base directory uses `PERENE_RESOURCES`, and fails clearly when it is missing
  - `~/Music` is used when present, otherwise home.
- `AudioExportTests` (fake `HttpMessageHandler` plus a fake `IHostShell` plus a temp directory):
  - each of the three href shapes calls the right worker route
  - an unknown href is rejected without calling the worker
  - a cancelled pick makes no request
  - success writes exact bytes, leaves no `.part` and sets `Notice`
  - a mid-stream failure deletes `.part` and sets `Error`
  - an existing destination file is replaced.
- `LocalSourceFilesTests` (temp directories):
  - recursive `.txt` collection
  - non-`.txt` files, `.DS_Store` and `._*` files counted as skipped
  - symlinked directories not followed
  - cap at `MaxFiles + 1`
  - `BoundedReadStream` throws `IOException` past the limit even if the file grows after measurement.
  - Through `TtsClient.CreateBatchAsync` with a recording handler, multipart part file names equal display names only (no `/`).
- `DesktopAccessMiddlewareTests` (`TestServer`/`DefaultHttpContext`):
  - no cookie → `403`
  - `/?launch=<right>` → `302` plus a `Set-Cookie` that has `HttpOnly` and `SameSite=Strict`
  - wrong launch value → `403`
  - a valid cookie passes.
- Existing `BatchRulesTests` and `TtsClientBatchTests` still pass. A new `BatchRules.ArchiveName` case is added.

**Build-script checks (in `packaging/macos/build.sh`, which fail the build):**
- `preflight` names any missing builder tool (it runs only in the builder image).
- Every download (standalone Python, FFmpeg, LAME, zig, rcodesign) is checked against a pinned SHA-256.
- The `packages` step installs only macOS arm64 wheels (`--platform macosx_14_0_arm64 --only-binary=:all:`); the two source-only requirements are pure-Python wheels.
- The `model` step re-hashes every file against `model-manifest.json`.
- The `ffmpeg_build` step checks the embedded configuration has `--enable-libmp3lame` and no `--enable-gpl`.
- The `check` step validates architecture, minimum macOS and library paths for every Mach-O file with `llvm-objdump`.
- The `sign` step confirms an ad-hoc signature on the main executable, FFmpeg and Python. Running the worker test suite against the bundled FFmpeg is not possible in Linux, so the FFmpeg commands are exercised by `make desktop-smoke` on the Mac.

**Integration tests:**
- `make desktop-smoke` (FR23) is the offline end-to-end test of the packaged worker with the real model, run once with `TTS_DEVICE=cpu` and once with the shipped default.
- ⚠️ TODO: there is no automated UI-level test of the Photino window. Window, dialogs, close confirmation, playback and Gatekeeper behaviour are verified manually on the Mac.

## Manual Verification
On this Mac (MacBook Air M4, macOS 26.6.2), from a clean checkout:

1. **Existing workflows are unchanged.** On any machine, run `make docker-check` and `make test` (pytest, xUnit, runtime compile all pass) and `make docker-run-bg`. Create a voice and generate speech at http://localhost:8082, download via the browser, then run `make docker-down`. On the Mac, confirm `make mac-worker` still starts.
2. **Build.** Run `make desktop-mac` (Docker, any machine), then `make desktop-smoke` on the Mac. Note the printed sizes and times in [Measurements](#measurements).
3. **Clean data.** Make sure no previous data exists: `ls ~/Library/Application\ Support/Perene\ TTS` fails. Turn **Wi-Fi off**.
4. **Install and launch.** Unzip the zip, drag the app to Applications and double-click it.
   - The window appears within 10 s with title "Perene TTS", and the Dock shows the icon.
   - The status shows "Loading voice engine…", then "Voice engine ready · MPS" (or "· CPU"). Time this.
5. **Second instance.** Double-click the app again: the existing window comes forward. Run the executable from Terminal and confirm the "already running" alert.
6. **Create a voice.** Use the native dialog to create one from a real MP3, and again from a WAV, in each of `en`, `pt` and `sv`. The preview plays inside the window.
7. **Generate speech.** Generate audio. Download the MP3 through the save dialog to `~/Music`, then play the saved file in Music or QuickTime.
8. **Batch.** Open Batch audio. With **Choose folder**, pick a copy of the `in-milton-lumky-territory` folder, keep intro and outro, and start the batch.
   - Close the window: the dialog appears. Select **Keep working**, and confirm conversion continues.
   - Press ⌘Q and record what happens (dialog, or the approved fallback).
   - Close again and select **Stop conversion and quit**. The app quits and `pgrep -f app.desktop` is empty within 10 s.
9. **Relaunch.** The batch shows `stopped` with completed tracks kept. Retry it and let it complete.
   - Download one track and **Download all** (ZIP) through the save dialogs. Check the names and that the ZIP opens in Finder.
   - Check My creations rows and their download buttons.
10. **Pause and quit.** Pause a running batch, then close the window: the app quits without a dialog. Relaunch, and the batch is still `paused`. Resume it.
11. **Crash recovery.** `kill -9` the worker while the window is open, and confirm the "stopped unexpectedly" message. Quit and reopen the app, and confirm it works.
12. **Security.** In Terminal, run `curl -i http://127.0.0.1:<web-port>/` (get the ports with `lsof -nP -iTCP -sTCP:LISTEN`) and expect `403`. Run `curl -i http://127.0.0.1:<worker-port>/health` and expect `401`. Confirm both listen only on `127.0.0.1`, and that macOS showed no firewall prompt.
13. **Data persistence.** Inspect `~/Library/Application Support/Perene TTS` (mode 700, data present) and `~/Library/Logs/Perene TTS/worker.log` (no source text, no network errors). Rebuild, replace the app in Applications, relaunch, and confirm the data persists.
14. **Friend install.** Send the zip to yourself with AirDrop, or download it from a cloud drive, so it arrives quarantined. Remove the installed app first. Install from that zip: the first open is blocked; **Open Anyway** in System Settings → Privacy & Security works; the engine becomes ready with Wi-Fi off. Repeat on a friend's Apple-silicon Mac when available, and record their macOS version.
15. **Accessibility.** Check keyboard-only use of the new buttons (pickers, downloads, close dialog with Escape = Keep working), VoiceOver labels on the download buttons, the notice announcement, and both themes.
16. **Cleanup.** Turn Wi-Fi back on.

## Measurements
Filled in during Phase 1b and Phase 4 on this Mac. The figures are recorded, not gated, except that the device decision uses the CPU vs MPS rows.

| Metric | Value |
| --- | --- |
| `Perene TTS.app` size | ⚠️ TODO |
| Zip size | ⚠️ TODO |
| Bundled Python with packages size | ⚠️ TODO |
| Model directory size | ⚠️ TODO (about 3.0 GB expected) |
| .NET self-contained publish size | ⚠️ TODO |
| FFmpeg size | ⚠️ TODO |
| Double-click → window visible | ⚠️ TODO (target ≤ 10 s) |
| Double-click → `model_ready` (CPU / MPS) | ⚠️ TODO |
| Speech: seconds of audio per second of compute (CPU / MPS) | ⚠️ TODO |
| Peak RSS (worker + host) during speech (CPU / MPS) | ⚠️ TODO |
| `make desktop-mac` build time (cold / warm cache) | ⚠️ TODO |
| Friend Mac tested (model, macOS version, result) | ⚠️ TODO |

## Definition of Done
- Requirements, Plan and Validation in this folder reflect the implemented behaviour. The Phase 1 spike findings (⌘Q routing, alert method, worker location in the bundle, final `collect_all` list, device decision, quarantine result) are written into Plan.md.
- `make test` passes: pytest, `dotnet test` for `WebApp.Tests` and the runtime-stage compile. The Docker and `mac-*` workflows are verified unchanged (FR24).
- New worker behaviour is covered by pytest, and new .NET rules and services are covered by xUnit, following the existing fake-engine and fake-handler conventions.
- `make desktop-mac` succeeds in Docker, `make desktop-smoke` succeeds on this Mac with nothing installed, and every Manual Verification step passes with Wi-Fi off.
- UI changes reuse the existing components (`ConfirmDialog`, card and button classes) and `app.css` tokens. They cover disabled/"Choosing…", cancel, error, success-notice and keyboard/VoiceOver states in both themes. No CDN or frontend package is added.
- Vendor decisions cite the Microsoft Learn evidence in Plan.md. The Photino items marked pending are resolved by Phase 1a, and the results are recorded.
- The app contains `THIRD-PARTY-NOTICES.md` and license texts. The model-license Open Question is answered before the zip is shared with anyone.
- README.md and AGENTS.md are updated. `build/` and `dist/` are ignored, `packaging/macos/spike/` is removed, and no app, zip, weights, recordings or generated audio are committed.

## Rollback Plan
- **Desktop app:** move `/Applications/Perene TTS.app` to the Trash and delete `dist/` and `build/`. User data can be removed with `rm -rf ~/Library/Application\ Support/Perene\ TTS ~/Library/Logs/Perene\ TTS ~/Library/Caches/Perene\ TTS`, which affects nothing used by Docker or `mac-worker` mode. Friends do the same with their copy.
- **Code revert, cleanest order:**
  - remove `packaging/macos/` and the `desktop-*` Make targets
  - remove `WebApp/Desktop/` and the desktop branch in `Program.cs`
  - remove the `Photino.NET` package reference.

  `StudioApp`, `IHostShell`/`BrowserHostShell`, `DownloadButton` and `ISourceFile` can stay, because in web mode they render and behave exactly as before.
- **Worker:** the `ApiToken` middleware, `TTS_MODEL_DIR` and `TTS_FFMPEG` are inert unless their environment variables are set, which Docker and `mac-worker` never do. They can stay, or be reverted along with `app/desktop.py` without touching stored voices, batches or the model cache.
- **Data:** desktop data lives only under `~/Library/…/Perene TTS`, Docker data only in the `tts-data`/`tts-models` volumes, and `mac-worker` data in its ignored `data/` and `models/` folders. No rollback touches the others. Never use `make docker-reset` for this.
