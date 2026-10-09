# Requirements: Batch Text-to-Audio Jobs

## Table of Contents
- [Problem Statement](#problem-statement)
- [Background and Reference Implementations](#background-and-reference-implementations)
- [User Stories](#user-stories)
- [Functional Requirements](#functional-requirements)
- [Non-Functional Requirements](#non-functional-requirements)
- [Out of Scope](#out-of-scope)
- [Open Questions](#open-questions)

## Problem Statement
Perene TTS can only synthesize one bounded text at a time. `GenerateAudio.razor` and `POST /speech` cap text at 5,000 characters (`MAX_TEXT` in `services/ChatterboxTtsService/app/studio.py`, `SpeechRequest` in `app/main.py`). Job state lives only in the in-memory `StudioService._jobs` dictionary, which is capped at 100 entries. `_start` rejects any second operation with `BusyError`. A real book export, such as `book-notes-ia/services/EbookParseService.Api/data/output/in-milton-lumky-territory/`, has 18 UTF-8 `.txt` files: an intro, `chapter-001.txt`…`chapter-016.txt`, and an outro. Together they hold about 436,000 characters, and each chapter is 18–39 KB. That is many hours of CPU synthesis on the Steam Deck. Today the user cannot upload such a set, cannot leave the browser while it converts, and cannot pause and continue it later. They cannot retry failed parts or get one audio file per text file. The sibling `book-notes-ia` `ChatterboxTtsService` already proves resumable per-file conversion, but only as a terminal command (`make create-audio-book`) with no pause, retry, or web interface.

## Background and Reference Implementations
This feature combines two existing designs. It does not modify either sibling project.

- **book-notes-ia `ChatterboxTtsService` audiobook batch (terminal)** provides the conversion concept to reuse.
  - `audiobook_project.py` defines the track model: intro → contiguous `chapter-NNN.txt` → outro, with `<Book name> NNN` output names.
  - `audiobook_manifest.py` holds the durable `audiobook-manifest.json`, with source/output SHA-256, model/voice compatibility, and per-track status.
  - In `audiobook_service.py`, a rerun skips verified completed tracks. Each track is written to a temporary file, validated, and atomically published.
  - Its limitations: it is CLI-only, English-only, and resumes only at whole-file granularity. Its only "pause" is `Ctrl+C`, which loses the in-progress chapter.
- **perene-archive job architecture** provides the job lifecycle and the web experience to reuse.
  - `Specs/20260917204553-conversion-job-pause-stop-controls/` adds `Paused`/`Stopped` states, Pause/Resume/Stop controls, a Stop confirmation modal, race-safe transitions, and "a paused job still occupies the worker".
  - `Specs/20260930164824-durable-jobs-and-naming-counters/` adds durable job status, Retry that resets the same job, and the one-line `JobRow.razor` list with tested `JobListRules` and paging.
  - Archive marks interrupted jobs as Failed on restart. This feature follows book-notes-ia instead and resumes them.

## User Stories
- Given a folder of book `.txt` files, when I choose the folder (or multi-select the files) on the Batch audio page, enter a name, and choose a saved voice, then I see the files in narration order with the MP3 name each will produce, and I can start the batch.
- Given a running batch, when I close the browser or navigate away, then conversion continues, and when I return the page shows current track, chunk, overall progress, and estimated time remaining.
- Given a running batch, when I select Pause, then it stops after the current chunk, and when I later select Resume (even after the worker restarts), it continues from the next unsynthesized chunk instead of restarting the chapter.
- Given a batch where one file failed, when the others finish and I select Retry, then only the failed and pending tracks are regenerated, and completed MP3s are kept.
- Given completed tracks, when I open the batch details, then I can play or download each MP3 individually or download all completed tracks as one ZIP.
- Given completed batch tracks, when I open My creations, then each track appears there with a tag naming its batch and position, and selecting the tag shows only the tracks from that batch.

## Functional Requirements
1. FR1 — Add an Interactive Server page `WebApp/Components/Pages/BatchAudio.razor` at `/batch-audio` and a "Batch audio" link in `MainLayout.razor`. It uses the existing card layout, design tokens, typography, and dark/paper-light themes.
2. FR2 — The page accepts `.txt` files through an `InputFile` with `multiple` and through a separate "Choose folder" picker (`webkitdirectory`). Only `.txt` files are kept. Skipped non-text files are counted in a notice. A new selection replaces the previous one.
3. FR3 — Before starting, the page shows the selected files in a default narration order. The order is: names ending in `intro` (case-insensitive, before `.txt`) first, then `chapter-NNN.txt` files in numeric order, then any other files in natural filename order, then names ending in `outro` last. The user can move a file up or down or remove it. Each row shows its position and its future output name `<Batch name> NNN.mp3`.
4. FR4 — Starting a batch requires a batch name (1–80 characters, no `/`, `\`, or control characters, not only whitespace) and a saved voice. The batch synthesizes every file in that voice's saved language (`en`, `pt`, or `sv`), the same way `POST /speech` does.
5. FR5 — Batch input bounds are 1–200 files. Each file must be valid UTF-8 (a leading BOM is ignored) with 1–200,000 non-whitespace-trimmed characters and at most 1 MiB, and the batch may total at most 10 MiB. The web page pre-checks these bounds and the worker enforces them. If any file is invalid, the whole batch is rejected with the file's display name and a concise reason.
6. FR6 — `POST /batches` on the worker stores server-named copies of the sources (`sources/NNN.txt`) and a durable, schema-validated `manifest.json` under `<TTS_DATA_DIR>/batches/<batch-uuid>/`. It returns `202` with a `queued` batch summary. Client file names are kept only as sanitized display labels and never as paths.
7. FR7 — A single background runner inside the worker process converts queued batches in FIFO order, one batch at a time and one track at a time, in the stored order. Conversion does not depend on any browser circuit, page, or HTTP request staying open.
8. FR8 — Each source file produces exactly one MP3 track named `<Batch name> NNN.mp3` (zero-padded position, three digits). A track is checked against its recorded source SHA-256 before synthesis. Its assembled WAV is validated with `validate_output` and encoded to MP3 at 192 kbps, using the same `libmp3lame` settings as `StudioService._render`, because quality takes priority. Its output is published atomically, and its SHA-256 and duration are recorded in the manifest.
9. FR9 — Each synthesized chunk of the current track is saved atomically as a checkpoint, using the deterministic seed `seed + chunk index`. A resumed track continues from the first chunk with no checkpoint. Final assembly inserts the same sentence (180 ms) and paragraph (420 ms) silences as single-text synthesis, so a resumed track matches an uninterrupted one.
10. FR10 — Pause moves a `running` batch to `pausing`, and then to `paused` after the current chunk's checkpoint is saved. A `queued` batch moves straight to `paused`. A paused batch holds no model lock, so other batches and studio operations can run. Resume moves a `paused` batch back to `queued`, and it continues from its next unsynthesized chunk.
11. FR11 — Stop is available for `queued`, `running`, `pausing`, and `paused` batches, and only after an accessible confirmation modal. The modal states that the current track's partial progress will be discarded, completed tracks will be kept, and the original text files are unaffected. Stop ends in the retryable terminal state `stopped`, and no partial MP3 is published.
12. FR12 — If a track fails (synthesis, validation, or encoding), it is marked `failed` with a concise user-facing message, and the runner continues with the remaining tracks. When no track is left to run, the batch ends `completed` if every track completed, otherwise `failed`.
13. FR13 — Retry on a `failed` or `stopped` batch resets the same batch to `queued` and regenerates only tracks that are not verified as completed. A completed track counts as verified when its MP3 exists and matches its recorded SHA-256. Retry on one `failed` track queues just that track, and its batch moves back to `queued` if it was terminal.
14. FR14 — When the worker starts, `running`, `pausing`, and `queued` batches become `queued` and continue automatically, with no user action, once the model is ready, resuming from checkpoints. `stopping` becomes `stopped`. `paused`, `stopped`, `failed`, and `completed` batches are unchanged. Checkpoints that are unreadable or incompatible (different model/source revision, voice, language, or seed) are discarded, and that track restarts.
15. FR15 — While a batch is running, voice creation and `POST /speech` are not rejected because of the batch. The interactive job waits until the current chunk finishes, with the message "Waiting for the batch to reach a safe point". It then runs with priority, and afterwards the batch reloads its own voice conditioning before its next chunk. A second concurrent interactive operation still receives the existing `409` busy response.
16. FR16 — The batch list shows, for each batch: state badge, name, voice, tracks completed/total, overall percent (by completed chunks across all tracks), current track display name with chunk `n/m`, elapsed time, and estimated time remaining from the measured average chunk time. `StudioSession` polls batch summaries with its existing health refresh. While a batch is active, the sidebar shows a short status (for example "Batch · 4/18 tracks").
17. FR17 — The batch details list every track with its display name, output name, state, duration, and failure message. Completed tracks get an `AudioPlayer`, a per-track MP3 download, and a per-track Retry when failed. "Download all" returns a ZIP of all currently completed tracks named `<Batch name>.zip` with `<Batch name> NNN.mp3` entries.
18. FR18 — Delete is available for batches that are not `running`, `pausing`, or `stopping`, after a confirmation modal. It permanently removes that batch's sources, checkpoints, tracks, and manifest.
19. FR19 — Batches are listed newest first and survive web and worker restarts. The page provides empty ("No batches yet"), loading, worker-unavailable, and validation-error states consistent with existing pages.
20. FR20 — The worker exposes these routes:
    - `GET /batches`
    - `POST /batches`
    - `GET /batches/{id}`
    - `POST /batches/{id}/pause`, `/resume`, `/stop`, and `/retry`
    - `POST /batches/{id}/tracks/{number}/retry`
    - `DELETE /batches/{id}`
    - `GET /batches/{id}/tracks/{number}/audio`
    - `GET /batches/{id}/archive`

    IDs must be canonical UUIDs and track numbers must be in range. Unknown IDs return `404`, and invalid state transitions return `409` with a concise message. Responses never include filesystem paths or source text.
21. FR21 — Setting `TTS_BATCH_ENABLED=false` disables the runner and makes `POST /batches` return `503`. Existing batches remain listable and downloadable. README.md and AGENTS.md document the workflow, limits, storage, and flag.
22. FR22 — `GET /creations` also returns every completed batch track as a creation with `kind: "batch"`. Each entry has a unique `job_id` of `<batch-uuid>-<NNN>`, plus `batch_id`, `batch_name`, `track_number`, `track_count`, `source_name` (sanitized display label), voice name, language, and `created_at` (the track's completion time). Its `text` is empty, so chapter text is never returned. In `Creations.razor`, these rows show a "Batch · `<batch name>` · NNN/MMM" tag, play and download through the batch track route as `<batch name> NNN.mp3`, and show the source file name and an "Open batch" link (`/batch-audio?batch=<id>`) in their details. Selecting the tag filters the Audio list to tracks from that batch, using a clearable filter chip. Search also matches batch and source names. A track leaves My creations when its batch is deleted or the track is reset for retry, and comes back when it completes again. Existing preview, speech, and legacy creations are unchanged.
23. FR23 — Add a `WebApp.Tests` xUnit project (net10.0) that references `WebApp/WebApp.csproj` and is run by `make test` inside Docker. It covers:
    - `BatchRules`: default order, output names, the `Can*` action rules, percent, and remaining time.
    - The batch filter and audio-URL rules in My creations.
    - The new `TtsClient` batch calls, using a fake `HttpMessageHandler`: multipart part order, error `detail` mapping, and canonical route formatting.

## Non-Functional Requirements
- **Process model:** Exactly one Uvicorn worker process. The batch runner is one daemon thread in the same process. Model access (conditioning selection plus synthesis) is serialized by one gate shared with `StudioService`, and no new Uvicorn workers or external queue are added.
- **Pause latency:** Pause and Stop take effect within one chunk (≤ 280 characters of synthesis). Inference is not interrupted mid-chunk, and the worker never sends signals to its own process.
- **Durability:** Manifest writes are atomic (temp file + `fsync` + `os.replace`) and re-validated after write, as in book-notes-ia `AudiobookManifestRepository.save`. A crash at any point leaves either the previous or the next valid manifest, and at most one chunk of work is lost.
- **Security and privacy:** Uploaded names are never used as paths. All batch paths are server-generated under `<TTS_DATA_DIR>/batches/<uuid>/` and checked for containment. Source text is never logged or returned. Logs contain batch ID, track number, and chunk counters only. `Content-Disposition` names come from the sanitized batch name. This continues the local, unauthenticated, localhost-only model.
- **Performance:** Uploading and validating the 18-file example (~436 KB) completes within a few seconds. Batch list polling returns summaries only (no track arrays, no text). Downloads and the ZIP stream from disk without loading whole files into memory.
- **Design:** Follow the existing `SynthesisEngine` protocol and single-responsibility modules. Project/validation, manifest/repository, runner, and audio assembly are separate, fake-testable units. The web keeps HTTP in `TtsClient`, wire models in `Contracts.cs`, circuit state in `StudioSession`, pure presentation rules in a small C# rules class, and styles in `wwwroot/app.css`.
- **Testability:** Worker behavior is covered with pytest, `TestClient`, a fake engine, and real FFmpeg, without model downloads, as in `tests/test_studio.py`. Web rules and client behavior are covered with xUnit in `WebApp.Tests`, run in Docker only, so the host still needs no .NET SDK.
- **Audio quality:** Batch tracks keep the 192 kbps MP3 encoding used today. The book-notes-ia POC produced lossless WAV with very good results, so the batch must not degrade below current speech quality.
- **Accessibility:** Controls have at least 40px targets and accessible names. Progress uses `role="progressbar"` with values. Status changes use polite live regions, and the confirmation modals are keyboard-operable.

## Out of Scope
- Reading server-side folders by path, mounting the book-notes-ia output directory, or any client-supplied filesystem path (forbidden by AGENTS.md).
- Text normalization or parsing of EPUBs (book-notes-ia `EbookParseService` remains responsible for producing narration-ready `.txt`).
- Per-file voice or language selection, adjustable seed/silence/exaggeration, or editing text in the browser.
- Combining tracks into one audiobook file, chapter metadata/ID3 tags, M4B output, or cover art.
- Reordering queued batches, parallel batches, multiple Uvicorn workers, or GPU-specific scheduling.
- Regenerating an already-completed, verified track, and authentication or multi-user isolation.

## Open Questions
No open questions block implementation. The user resolved the discovery questions on 2026-10-08:
- **Bitrate:** keep 192 kbps MP3 for quality (FR8). The book-notes-ia POC used WAV, and quality is the priority.
- **Restart behavior:** interrupted batches continue automatically after a worker restart (FR14).
- **My creations:** completed batch tracks appear in My creations with a batch tag (FR22).
- **.NET tests:** add a `WebApp.Tests` xUnit project in this slice (FR23).
- **CPU throughput:** no separate target-hardware throughput study is needed. The book-notes-ia CPU POC already proved that long-form synthesis is viable on this hardware. The remaining-time estimate is still computed at runtime for display (FR16).
