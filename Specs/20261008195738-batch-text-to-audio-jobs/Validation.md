# Validation: Batch Text-to-Audio Jobs

## Table of Contents
- [Acceptance Criteria](#acceptance-criteria)
- [Test Cases](#test-cases)
- [Manual Verification](#manual-verification)
- [Definition of Done](#definition-of-done)
- [Rollback Plan](#rollback-plan)

## Acceptance Criteria

| Requirement | Acceptance Criterion |
| --- | --- |
| FR1 | The sidebar shows "Batch audio" and highlights it at `/batch-audio`. The page renders with the existing card styles in dark and paper-light themes and is usable at mobile width without horizontal scroll. |
| FR2 | Selecting the example folder through "Choose folder" lists the 18 `.txt` files. Adding a `.jpg` to the selection shows "1 file skipped (not .txt)". A second pick replaces the list. Multi-select through the file picker produces the same list. |
| FR3 | For the example folder, the default order is `InMiltonLumkyTerritoryIntro.txt`, `chapter-001.txt` … `chapter-016.txt`, `InMiltonLumkyTerritoryOutro.txt`. Rows show `<name> 001.mp3` … `<name> 018.mp3`. Move up/down/remove update positions and output names immediately. |
| FR4 | Start is disabled without a valid name or voice. A name containing `/` is rejected. A batch started with a Portuguese voice records `language_id = "pt"` in its manifest. |
| FR5 | 201 files, a 1 MiB + 1 byte file, a non-UTF-8 file, or a whitespace-only file each produce a specific message naming the file, from the page and from a direct `POST /batches` (`422`/`413`). No batch directory is created. |
| FR6 | After creation, `/data/batches/<uuid>/` contains `manifest.json` and `sources/001.txt…018.txt` only. No client file name appears in any path, and the response state is `queued`. |
| FR7 | With the browser closed, `GET /batches/{id}` keeps showing `chunks_done` increasing. Two created batches run one after the other in creation order. |
| FR8 | Every completed track has `tracks/NNN.mp3` whose SHA-256 equals `output_sha256`, a positive `duration_seconds`, and downloads as `<name> NNN.mp3`. A source edited on disk after creation fails its track with a "source changed" message. |
| FR9 | A track interrupted after chunk k and resumed synthesizes only chunks k+1…n (fake engine records `seed_offset` values with no duplicates). The assembled WAV equals the output of a single uninterrupted multi-chunk fake call, frame for frame. |
| FR10 | Pause on a running batch returns `pausing`, then `paused` after the current chunk. An interactive `POST /speech` then runs immediately. Resume returns `queued`, then `running` from the next chunk. Pause on a queued batch returns `paused` directly. |
| FR11 | Stop opens a modal with the three required statements, and Cancel sends nothing. Confirming moves to `stopping`/`stopped`, `work/NNN/` is removed, completed MP3s remain, and no MP3 exists for the interrupted track. |
| FR12 | With the fake engine failing track 2 of 3, tracks 1 and 3 complete, track 2 is `failed` with a concise message (no internal path), and the batch ends `failed`. All tracks succeeding ends `completed`. |
| FR13 | Retry on that batch regenerates only track 2 (engine call count proves it). Tampering with track 1's MP3 before Retry causes it to regenerate as well. Per-track Retry on a failed track queues only that track. |
| FR14 | Killing the worker mid-chunk and restarting it turns the `running` batch into `queued` and then `running`, and it continues from the last saved checkpoint. A `paused` batch remains paused. A `stopping` batch becomes `stopped`. A checkpoint written with a different seed or voice is discarded. |
| FR15 | During a running batch, creating a voice returns `202`, and its job message reads "Waiting for the batch to reach a safe point" until the chunk ends. The preview then completes, and the batch's next chunk reloads batch conditioning (fake engine load count increments). A second simultaneous interactive request returns `409`. |
| FR16 | The row shows state, `n/m tracks`, percent, current track and chunk, elapsed, and estimate. Values update without reload (5 s polling). The sidebar shows "Batch · n/m tracks" only while a batch is active. `GET /batches` contains no `tracks` array and no text. |
| FR17 | Expanding a batch lists all tracks. Completed tracks play inline and download. "Download all" returns `<name>.zip` whose entries are exactly the completed `<name> NNN.mp3` files with matching checksums. |
| FR18 | Delete is not shown for running/pausing/stopping batches, and the API returns `409` for them. After a confirmed delete of a paused batch, its directory is gone and `GET /batches/{id}` returns `404`. |
| FR19 | After `make docker-down && make docker-run-bg`, all batches and completed tracks are still listed newest first. Empty, loading, and worker-unavailable states render as specified. |
| FR20 | Non-canonical UUIDs and out-of-range track numbers return `404`/`422`. Invalid transitions return `409` with a concise `detail`. No response body or worker log line contains `/data`, source text, or uploaded file contents. |
| FR21 | With `TTS_BATCH_ENABLED=false`, `POST /batches` returns `503`, the page shows the disabled explanation, existing batches remain downloadable, and no runner thread is started. README.md and AGENTS.md describe the feature. |
| FR22 | Completed batch tracks appear in My creations, newest first, with the "Batch · `<name>` · NNN/MMM" tag. They play and download as `<name> NNN.mp3`. `GET /creations` returns `text: ""` for them. Selecting the tag shows only that batch's tracks until the chip is cleared. "Open batch" opens `/batch-audio?batch=<id>` with that batch expanded. Deleting the batch removes its rows. Preview, speech, and legacy rows look and behave as before. |
| FR23 | `make test` runs `dotnet test` on `WebApp.Tests` in Docker, and all `BatchRulesTests` and `TtsClientBatchTests` pass. A deliberately broken rule (for example, outro not last) makes `make test` fail. |

## Test Cases

**Unit tests (pytest, `services/ChatterboxTtsService/tests/`, fake engine, real FFmpeg, following `test_studio.py`/`conftest.py`):**
- `test_batch_project.py`:
  - name rules and file count/size/total bounds
  - UTF-8 with BOM accepted; invalid UTF-8 and whitespace-only files rejected with the display name
  - display-name sanitizing (path components, control characters)
  - `<name> NNN.mp3` naming
  - chunk counts equal `len(chunk_text(text, 280))`.
- `test_batch_manifest.py`:
  - round-trip save/load
  - exact field-set rejection of unknown/missing fields
  - invalid state, digest, or ordering rejected
  - atomic save leaves the previous manifest intact when serialization fails
  - containment check rejects escaped paths.
- `test_wav_assembly.py`: chunk + 180/420 ms silence assembly equals the frames produced by a multi-chunk fake synthesis, and no trailing silence is added.
- `test_operation_gate.py`:
  - a second interactive op raises `BusyError`
  - `batch_step` yields to a waiting interactive op
  - `conditioning_owner` changes are tracked.
- `test_batch_runner.py`:
  - happy path (3 tracks → 3 MP3s, manifest `completed`)
  - deterministic `seed_offset` sequence
  - pause at a chunk boundary, then resume without duplicate chunks
  - stop discards `work/NNN/` and keeps completed tracks
  - one track fails and the rest continue
  - retry regenerates only unverified tracks
  - source checksum mismatch fails the track
  - recovery mapping on a fresh runner over the same directory
  - incompatible checkpoint discarded
  - model not ready keeps the batch `queued`
  - late progress does not overwrite `pausing`/`stopping`.
- `test_studio.py` (updated):
  - `FakeEngine.synthesize` accepts `seed_offset`
  - existing preview/speech/creations tests still pass
  - the busy test asserts `409` only for a second interactive op
  - `/creations` includes completed batch tracks (`kind: "batch"`, unique `job_id`, empty `text`, batch fields), excludes pending/failed tracks, and emits `null` batch fields for preview/speech/legacy entries.

**Integration tests (FastAPI `TestClient` over a real temp data dir, `test_batch_api.py`):**
- Multipart `POST /batches` (202, 413, 422), list/detail shapes, pause/resume/stop/retry/track-retry/delete transitions including `409` cases, `404` for unknown IDs, and track audio download with `Content-Disposition` naming.
- ZIP archive entries and checksums.
- `TTS_BATCH_ENABLED=false` returns `503`.
- `caplog` assertion that source text never appears in logs.

**Web unit tests (xUnit, new `WebApp.Tests/`, run by `make test` through the Dockerfile `web-test` stage):**
- `BatchRulesTests.cs`:
  - `DefaultOrder` on the exact 18 example names gives intro → chapter-001…016 → outro
  - case-insensitive `Intro`/`OUTRO`
  - `chapter-2` sorts before `chapter-10` in natural order
  - unknown names land between chapters and outro
  - `OutputName("Lumky", 7)` returns `Lumky 007.mp3`
  - a `[Theory]` over every state for `CanPause/CanResume/CanStop/CanRetry/CanDelete`
  - `Percent` with zero chunks, and `Remaining` with no timing data returns null
  - `AudioUrl` for batch and non-batch creations
  - `MatchesBatch`, and the `TagLabel` format.
- `TtsClientBatchTests.cs`:
  - a fake `HttpMessageHandler` verifies the `POST batches` multipart fields `name`, `voice_id`, and repeated `files` in the submitted order
  - pause/resume/stop/retry/track-retry/delete paths use canonical `D`-format GUIDs
  - a `409` body `{"detail":"…"}` becomes an `InvalidOperationException` with that message
  - `BatchesAsync` deserializes snake_case summaries
  - the `Creation` JSON without batch keys still deserializes (backward compatibility).

**Web compilation:**
- `make test` also compiles the .NET runtime stage, which proves `BatchAudio.razor`, `BatchRow.razor`, `ConfirmDialog.razor`, `Creations.razor`, `TtsClient`, `StudioSession`, and `Program.cs` build under nullable warnings.

**Full-stack (real model, manual on target hardware):** see Manual Verification. A CI-style real-model test is ⚠️ TODO and out of scope, because model downloads are excluded from tests.

## Manual Verification
1. From a clean checkout, run `make docker-check`, `make test` (all pytest and xUnit tests pass; the .NET runtime stage compiles), then `make docker-run-bg`, and wait until `make get-url` health reports `model_ready: true`.
2. Open http://localhost:8082. If no voice exists, create one on Create a voice.
3. Open **Batch audio**. Use **Choose folder** on `/home/deck/Documents/Projects/book-notes-ia/services/EbookParseService.Api/data/output/in-milton-lumky-territory`. Confirm the 18 files are in intro → chapter-001…016 → outro order with `Name 001.mp3`…`Name 018.mp3`. Repeat with the multi-file picker. Try moving and removing a row.
4. For a quick full run, remove all chapters (keep intro and outro), name the batch "Lumky smoke", choose the voice, and start. Confirm the `queued` → `running` → `completed` states, the progress, and the estimate. Play and download both tracks. Download all and inspect the ZIP entries.
5. Start the full 18-file batch. After a few chunks, close the browser tab, wait, and reopen it. Progress has advanced.
6. Select **Pause** and observe `pausing` → `paused` within one chunk. Generate a short speech on Generate audio, and confirm it runs immediately. Resume, and in `docker compose logs tts`, confirm the chunk counter continues and does not restart at 1.
7. While the batch is running, create a voice test, and confirm the "Waiting for the batch to reach a safe point" message, then completion. The batch then continues.
8. Run `docker compose -p perene-tts restart tts` mid-chunk. After the model loads, the batch returns to `running` and continues from its checkpoint. Pause, restart again, and confirm it stays `paused`.
9. Select **Stop** and verify the modal text, Cancel, then Stop → Confirm. Verify that completed tracks remain and `work/` is empty (`docker compose exec tts ls /data/batches/<id>`). Select **Retry**, and confirm only the remaining tracks run.
10. Delete the smoke batch through the modal, and verify it disappears and its directory is removed.
11. Open **My creations**. Check:
    - the completed batch tracks show the "Batch · Lumky smoke · 001/002" tag and play and download correctly
    - selecting the tag filters to that batch and the chip clears it
    - details show the source file name, and "Open batch" lands on the expanded batch
    - existing voice tests and generated audio rows are unchanged.

    After step 10, confirm that the smoke batch's rows are gone.
12. Check keyboard-only operation (Tab to the pickers, row actions, and modals; Escape closes a modal), screen-reader labels on the icon buttons, and layout at narrow width in both themes. Check the folder picker in Chromium and Firefox.
13. Set `TTS_BATCH_ENABLED=false` in the compose environment and restart. Verify the disabled state and that existing downloads still work. Restore the default.

## Definition of Done
- Requirements, Plan, and Validation in this folder reflect the implemented behavior.
- `make test` passes, covering pytest, `dotnet test` for `WebApp.Tests` (via the `web-test` stage), and the .NET runtime-stage compile.
- New worker behavior is covered by the pytest cases above, with a fake engine and real FFmpeg and no model downloads. New web rules and client calls are covered by xUnit.
- Batch tracks are encoded at 192 kbps with the same settings as single-text speech.
- The UI covers empty, loading, error, disabled, paused, and responsive states, with accessible labels, progress semantics, and keyboard-operable confirmation dialogs, using `app.css` tokens only and no CDN or new frontend package.
- The `SynthesisEngine` change is backward compatible (`seed_offset` defaults to 0), and pinned model/source revisions are unchanged.
- Upload handling follows the cited Microsoft Learn guidance (explicit `maxAllowedSize`, file-count cap, no client file names for storage, lazy per-file streams).
- README.md and AGENTS.md (via `init-agent`) document the page, limits, storage layout, restart/resume semantics, `TTS_BATCH_ENABLED`, and disk usage.
- No recordings, generated audio, uploaded text, or batch data are committed (`**/data/` and `*.mp3`/`*.wav` stay ignored).

## Rollback Plan
- **Immediate:** set `TTS_BATCH_ENABLED=false` for the `tts` service and restart it. The runner does not start, `POST /batches` returns `503`, and interactive voice/speech flows keep working through `OperationGate` exactly as before, because no batch ever holds the gate.
- **Code revert:**
  - remove the `Batch audio` `NavLink` in `MainLayout.razor`, `BatchAudio.razor`, `BatchRow.razor`, and the `/batches/...` proxy routes in `Program.cs`
  - remove the `/batches` routes and runner startup in `app/main.py`, and the `batches.creations()` merge in the `/creations` route (My creations then shows only preview/speech/legacy audio again; the nullable `Creation` batch fields can stay)
  - `WebApp.Tests` and the `web-test` stage can stay, minus the batch-specific tests.
  - keep or revert `OperationGate`; restoring the previous `threading.Lock` in `StudioService._gate` restores the original immediate-`409` behavior.

  `seed_offset` defaults to `0` and can stay.
- **Data:** batch data lives only in `<TTS_DATA_DIR>/batches/` on the `tts-data` volume. Leaving it is harmless. Deleting that directory removes all batch data without touching voices, outputs, or models. Never use `make docker-reset` for this, because it deletes voices and models too.
