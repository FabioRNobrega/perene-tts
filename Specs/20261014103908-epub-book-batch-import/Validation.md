# Validation: EPUB Book Batch Import

## Table of Contents
- [Acceptance Criteria](#acceptance-criteria)
- [Test Cases](#test-cases)
- [Manual Verification](#manual-verification)
- [Definition of Done](#definition-of-done)
- [Rollback Plan](#rollback-plan)

## Acceptance Criteria

| Requirement | Acceptance Criterion |
| --- | --- |
| FR1 | `/batch-audio` shows the tabs New batch, From EPUB, and Batches, in that order, with correct `role="tab"`/`aria-selected`/`aria-controls`. `/batch-audio?tab=epub` opens From EPUB. `?tab=batches` and `?batch=<id>` still open Batches, with the batch expanded for `?batch=`. |
| FR2 | Choosing a `.epub` parses it. A file over 100 MiB shows "The EPUB exceeds 100 MiB." No file remains under the container's `/tmp` after a success, a failure, or navigating away while parsing. No path is built from the client file name. |
| FR3 | Synthetic EPUB 3 (nav fragments, `epub:type="chapter"`) and EPUB 2 (NCX whole-document, fragment, and numbered-title) books produce the same chapters as the reference tests. Wrong or reordered mimetype, `encryption.xml`, a missing language, duplicate chapter numbers, an undeclared target, too many entries, and an oversized entry are each rejected with the reference message. |
| FR4 | After a successful import, choosing an invalid EPUB shows the error alert and the previously imported book, name, intro, outro, and selections stay unchanged. |
| FR5 | The summary shows the title, language, chapter count, and total characters. The batch name is prefilled from the title with `/`, `\`, and control characters removed and truncated to 80. Editing to an invalid name shows the `BatchRules.NameError` message and disables Start. |
| FR6 | The voice select lists saved voices. A `pt-BR` book with an English voice shows a warning naming both languages, and Start stays enabled. |
| FR7 | The intro and outro accept at most 5,000 characters and show a counter. A blank field adds no row. A filled field adds an `intro.txt` row first or an `outro.txt` row last, without a heading. |
| FR8 | Chapters are listed in book order and all are checked by default. Unticking removes the chapter from numbering and output names. Include none disables Start and shows "Include at least one chapter." Include all restores every chapter. |
| FR9 | The Preview toggle sets `aria-expanded` and reveals `#chapter-preview-N` with exactly the paragraphs to be uploaded. Collapsing removes the rendered text. Without a voice, the preview shows the paragraphs and the heading hint. |
| FR10 | With an English voice, chapter 24 starts `Chapter Twenty-four.`, then a blank line. With pt it starts `Capítulo Vinte e quatro.`, and with sv `Kapitel Tjugofyra.`. Chapter 1000 uses the digits `1000`. A `***` paragraph is removed. In pt and sv, a leading dialogue dash is removed, so `– Jaha. Vad gör vi nu då? sa Irene till Tommy.` becomes `Jaha. Vad gör vi nu då? sa Irene till Tommy.`. In sv, `– Kom hit, sa han. – Nu!` becomes `Kom hit, sa han. Nu!`. In en, dialogue dashes are kept. |
| FR11 | With an intro, chapters 1–3 (2 excluded), and an outro, the uploaded parts are, in order, `intro.txt`, `chapter-001.txt`, `chapter-003.txt`, `outro.txt`. The list shows `<Name> 001.mp3` to `<Name> 004.mp3`. |
| FR12 | An included chapter over 200,000 characters or 1 MiB is marked invalid with the `BatchRules` reason, and Start is disabled until it is excluded. More than 200 tracks or more than 10 MiB in total shows the `SelectionError` message. |
| FR13 | Start sends one `POST /batches` with the name, voice, and the parts from FR11. On a 202 the page shows Batches with the new batch expanded, the From EPUB draft is empty, and the voice is remembered. On a 422 or 503 the error is shown and the draft is intact. |
| FR14 | Switching to Batches and back, or to Generate audio and back, keeps the imported book, name, voice, intro, outro, inclusion, and open previews. Clear empties the tab. A browser reload starts empty. |
| FR15 | Each state renders: empty, "Reading EPUB…" (inputs disabled), parse error, batch disabled, no voices, model loading (Start disabled with hint), and "Uploading…". |
| FR16 | Web logs from a successful and a failed import contain no chapter text, intro or outro text, title, or file name. They contain only the error category, whether a repair ran, chapter count, and character totals. |
| FR17 | A conforming EPUB imports with no repaired note and no second temporary file. EPUBs whose `mimetype` is missing, not the first entry, or has a wrong value import successfully, with the same chapters as their conforming twin, and show "This EPUB's packaging was repaired automatically." A non-conforming archive with an unsafe entry path, duplicate paths, a symlink entry, a `mimetype/` directory, or over the entry or size limits is rejected with the matching message. A non-ZIP file is rejected with "The source is not a valid ZIP archive." No temporary file remains in any case. |

## Test Cases

Tests use the xUnit `WebApp.Tests` project (`[Fact]`/`[Theory]`, fake `HttpMessageHandler`s, no real books) and run in Docker through `make test` (the Dockerfile `web-test` stage).

**Unit tests:**
- `WebApp.Tests/Epub/EpubChapterParserTests.cs`: ported from `book-notes-ia/services/EbookParseService.Tests/EpubChapterParserTests.cs`, using `SyntheticEpubBuilder` streams.
  - Navigation fragments keep chapters isolated.
  - A semantic chapter with a non-numeric label is accepted.
  - NCX whole-document targets, fragment targets, and numbered titles are handled.
  - Following spine resources are included.
  - Mismatched headings are rejected.
  - `&nbsp;` is handled without an external DTD.
  - A missing NCX, a missing navMap, an undeclared target, or a duplicate target is rejected.
  - Inferred language and inconsistent inferred language are handled.
  - An EPUB 3 without language is rejected.
  - An encrypted publication is rejected.
  - Duplicate chapter numbers are rejected.
  - The entry limit is enforced.
  - A wrong or reordered mimetype passed *directly* to the parser, bypassing the repairer, is still rejected, which keeps the defense in depth.
  - New: `MaxBookCharacters` is enforced.
  - New: a non-seekable stream or a non-ZIP stream maps to `InvalidEpub`.
  - The input-name and symlink tests are dropped because they no longer apply.
- `WebApp.Tests/Epub/TtsTextNormalizerTests.cs`:
  - The ported en/pt cases.
  - sv cases: a leading dash is removed, an inline dash after terminal punctuation becomes a space, an inline dash without terminal punctuation becomes a comma, `***` is removed, and periods and whitespace are normalized.
  - The same dialogue line produces identical output under sv and pt.
  - en keeps dashes.
  - A null language gets language-neutral rules only.
  - An unsupported language is rejected.
- `WebApp.Tests/Epub/EpubArchiveRepairerTests.cs`: builds synthetic archives in memory, mirroring the `SyntheticEpubBuilder` variants.
  - A conforming archive returns `Repaired == false` and the same stream.
  - A missing marker, a reordered marker, a wrong marker value, and a compressed old marker that is not first are each rebuilt. In the result, entry 0 is `mimetype`, it is the only marker, its value is exact, other entry paths and bytes keep their original order, and the parser then succeeds.
  - Rejections: absolute path, `../`, backslash, drive letter, duplicate path, symlink (`ExternalAttributes` with `0xA000 << 16`), a `mimetype/` directory, entry count over the limit, an entry over `MaxEntryBytes`, and a total over the limit.
  - A non-ZIP stream gives "The source is not a valid ZIP archive."
  - Cancellation propagates.
  - Disposing `PreparedEpub` deletes the rebuilt temporary file.
- `WebApp.Tests/Epub/NumberToWordsConverterTests.cs`: ported en/pt cases, plus sv cases: 0 `noll`, 1 `ett`, 17 `sjutton`, 24 `tjugofyra`, 100 `etthundra`, 124 `etthundratjugofyra`, 200 `tvåhundra`, 999 `niohundranittionio`. The values −1 and 1000 return false.
- `WebApp.Tests/Epub/BookTrackBuilderTests.cs`:
  - Heading format per language, and no heading without a language.
  - Paragraphs are joined with `\n\n` and end with a trailing `\n`.
  - Intro and outro whitespace is normalized and the text is otherwise unchanged; blank values are skipped.
  - Excluded chapters are skipped.
  - Order and display names match FR11.
  - Characters and bytes match what is uploaded (UTF-8 with no BOM).
- `WebApp.Tests/Epub/EpubBookRulesTests.cs`:
  - `DefaultBatchName` sanitizes and truncates.
  - `ChapterLabel` per language.
  - `TrackFileName` padding.
  - `PrimaryLanguage` / `LanguageMismatch` (`pt-BR` versus `pt`, `en-GB` versus `pt`, empty).
  - `TrackErrors`: 200,001 characters, 1 MiB + 1 byte, 201 tracks, 10 MiB + 1 byte in total, and no chapters included.
- `WebApp.Tests/Epub/EpubImportServiceTests.cs`: uses a fake `IBrowserFile`.
  - An oversized stream (`IOException`) maps to `InvalidRequest` with the size message.
  - A valid synthetic EPUB parses with `Repaired == false`.
  - A reordered-mimetype EPUB parses with `Repaired == true`.
  - A cancelled token propagates `OperationCanceledException`.
  - The temporary file is deleted after success and failure. Assert by snapshotting `Path.GetTempPath()` for new files.
- `WebApp.Tests/TtsClientBatchTests.cs`: add one case where `CreateBatchAsync` sends in-memory `MemoryStream` parts named `intro.txt`, `chapter-003.txt`, and `outro.txt` in that order. This proves no `LazyBrowserFileStream` dependency.

**Integration tests:**
- Full-stack happy path, done as manual verification because there is no Blazor component or browser test harness in this repo. Use `make docker-run-bg` with the real worker: import a synthetic EPUB, start the batch, and confirm `GET /batches/{id}` lists the tracks in order with the display names from FR11.
- ⚠️ TODO: The repo has no bUnit or Playwright. Tab/ARIA/state rendering (FR1, FR9, FR14, FR15) is verified manually. Add component tests only if a later spec introduces a component test harness.
- The existing pytest suite (`services/ChatterboxTtsService/tests`) must pass unchanged, which confirms the worker contract did not change.

## Manual Verification
1. From the repo root, run `make test`. Pytest, `WebApp.Tests`, and the runtime-stage compile all pass.
2. Run `make docker-run-bg` and open http://localhost:8082/batch-audio (or the URL printed by `make get-url`). Wait for "Voice engine ready" and make sure at least one saved voice exists. If none exists, create one on Create a voice.
3. Confirm the three tabs appear in order. Open http://localhost:8082/batch-audio?tab=epub, and check that the From EPUB tab is selected and keyboard focus reaches the picker.
4. Generate a synthetic EPUB with a few chapters. Do not commit it. One way is to run the test builder in a scratch test, or use a public-domain DRM-free EPUB you are legally permitted to process, kept outside the repo. Choose it. "Reading EPUB…" appears, then the summary appears and the batch name is prefilled.
5. Choose a voice whose language differs from the book's language. The warning appears and Start stays enabled. Switch to a matching voice and the warning disappears.
6. Type an intro and an outro. The narration list shows the `intro.txt` row first and the `outro.txt` row last, with output names `001` … `N`.
7. Expand chapter 2's preview. It starts with the heading in words for the voice's language. The toggle announces expanded and collapsed with a screen reader or devtools `aria-expanded`. Collapse it.
8. Untick chapter 2. The numbering and output names shift. Select Include none: Start is disabled and the message appears. Select Include all.
9. Switch to Batches, then Generate audio, then back to From EPUB. Everything from steps 4–8 is still there.
10. Choose a non-EPUB file renamed to `.epub`. An error appears and the previous import remains.
    Then make a copy of the test EPUB with the `mimetype` entry moved last, for example in a scratch directory outside the repo: `unzip -q book.epub -d x && (cd x && zip -qr ../reordered.epub . -x mimetype && zip -q ../reordered.epub mimetype)`. Choose it. It imports with the same chapters and shows "This EPUB's packaging was repaired automatically."
11. Select Start book batch. The Batches tab opens with the new batch expanded. The track names are `intro.txt`, `chapter-001.txt`, …, `outro.txt`. Pause, Resume, and per-track playback work as for any batch. From EPUB is now empty.
12. Run `docker compose exec web ls -la /tmp`. No leftover random-named EPUB temporary files remain. Run `make docker-logs`. Web logs show no book text, title, or file name.
13. Toggle the paper-light theme and narrow the window to phone width. The tabs scroll horizontally, the preview region scrolls, and focus outlines are visible.
14. Optionally, set `TTS_BATCH_ENABLED=false` for the worker and restart. From EPUB shows the batch-disabled notice.

## Definition of Done
- Requirements, Plan, and Validation docs are updated in this spec folder, with the open questions resolved or deferred explicitly.
- All existing tests still pass (`make test`: pytest, `WebApp.Tests`, runtime compile).
- New repairer, parser, normalizer, number-words, track-builder, rules, and import-service behavior has xUnit coverage in `WebApp.Tests/Epub/` using synthetic EPUBs only.
- UI: `BatchAudio.razor`, `EpubBatchPanel.razor`, and `app.css` are updated consistently. The plan's responsive, empty, loading, error, and accessibility states are verified manually in both themes.
- No AI/agent behavior changed. The worker's synthesis, chunking, and batch runner are untouched.
- Vendor-specific decisions (InputFile streaming, ZIP limits, XML settings, SignalR bounds) cite Microsoft Learn in `Plan.md`.
- No new NuGet package, container, or Compose service. `README.md` documents the From EPUB workflow, and `AGENTS.md` is refreshed through the init-agent skill.
- The sibling `book-notes-ia` project is unmodified.

## Rollback Plan
- The feature is additive in the web host only. To revert it, remove the **From EPUB** tab button, the `Tab.Epub` branch, and `?tab=epub` mapping in `BatchAudio.razor`, and the `EpubBatchPanel` render. That alone hides the feature.
- Then remove the Epub service, options, and draft registrations in `WebApp/Program.cs`, and delete `WebApp/Services/Epub/`, `WebApp/Models/EpubBook*.cs`, `WebApp/Components/EpubBatchPanel.razor`, and `WebApp.Tests/Epub/`.
- No data migration is needed. Batches created from an EPUB are ordinary worker batches, and they stay listed, playable, and deletable after rollback. The worker, its manifests, and `TTS_DATA_DIR` are untouched.
- To disable all batch creation, including EPUB import, without a deploy, set the existing `TTS_BATCH_ENABLED=false` on the worker.
