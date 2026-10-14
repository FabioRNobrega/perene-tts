# Requirements: EPUB Book Batch Import

## Table of Contents
- [Problem Statement](#problem-statement)
- [Background and Reference Implementation](#background-and-reference-implementation)
- [Discovery Decisions](#discovery-decisions)
- [User Stories](#user-stories)
- [Functional Requirements](#functional-requirements)
- [Non-Functional Requirements](#non-functional-requirements)
- [Out of Scope](#out-of-scope)
- [Open Questions](#open-questions)

## Problem Statement
Today `WebApp/Components/Pages/BatchAudio.razor` only builds a batch from `.txt` files the user already has. To narrate a book, the user must first turn the EPUB into text outside Perene TTS. The only tool for that is the sibling `book-notes-ia/services/EbookParseService.Api`, a separate Docker API that reads from a private input folder. Its output is `chapter-NNN.txt` files, which the user then picks again in the New batch tab. Book intros and outros must be written as separate `*intro.txt` / `*outro.txt` files so that `BatchRules.DefaultOrder` puts them first and last. Nothing lets the user see what was extracted from each chapter before spending hours of CPU synthesis on it. This feature makes EPUB import native to the Batch audio page. The user picks one `.epub`, types an intro and outro, previews and selects chapters, and starts one batch for the whole book. The existing worker batch pipeline stays unchanged.

## Background and Reference Implementation
`book-notes-ia/services/EbookParseService.Api` (.NET 10, BCL only) is the reference. It is ported, not called, and the sibling project is not modified (AGENTS.md constraint).

- `Services/EpubChapterParser.cs` (961 lines) does strict chapter discovery:
  - It checks the `mimetype` marker, follows `META-INF/container.xml` to the package, and reads the manifest and spine.
  - It reads the EPUB 3 nav and accepts `epub:type="chapter"` or numeric labels confirmed by a matching heading.
  - It falls back to the EPUB 2 NCX, accepting labels such as `Chapter N`, `N`, or `N. Title` with a matching heading.
  - It enforces archive entry/size limits, secure XML settings, and encrypted-content rejection.
- `Services/TtsTextNormalizer.cs` handles `en`/`pt`. It drops `***` scene markers, normalizes runs of periods and whitespace, and applies Portuguese dialogue-dash rules.
- `Services/NumberToWordsConverter.cs` writes cardinal words for 0–999 in `en`/`pt`.
- `repair-ebook.sh` (`make repair-ebook BOOK=…`) is a manual, one-off container step. It rejects unsafe, duplicate, or symlink entries, rewrites the archive with a stored `mimetype` first, verifies the result, and writes `<name>-fixed.epub` for the user to parse in a second run.
- `Services/ChapterOutputWriter.cs` renders `"<Label> <number>.\n\n<paragraphs joined by blank lines>\n"`.
- The xUnit `EbookParseService.Tests` project includes `SyntheticEpubBuilder.cs`, which builds synthetic fixtures only.

## Discovery Decisions
| Topic | Decision |
| --- | --- |
| Chapter preview | Read-only preview plus an include/exclude checkbox per chapter. Chapter text cannot be edited. |
| Parser strictness | Strict, same as the reference. A book without explicit chapter structure fails with a clear message, and there is no table-of-contents fallback. |
| Spoken heading | Each chapter track starts with the label and number in words in the voice's language: `Chapter Twenty-four.`, `Capítulo Vinte e quatro.`, `Kapitel Tjugofyra.`. Swedish is added. |
| Over-limit chapter | A chapter over the existing per-track limits is shown as invalid and blocks Start until it is excluded. It is not split automatically. |
| Swedish dialogue | Swedish fiction marks dialogue the same way Portuguese does. Each line starts with an en dash (*talstreck*/*replikstreck*) followed by a space, there are no quotation marks, and the speaker tag follows the line (`– Jaha. Vad gör vi nu då? sa Irene till Tommy.`). Swedish therefore uses the Portuguese dash rules. Sources are in `Plan.md`. |
| Track numbering | Confirmed: positional numbering. When an intro exists, the intro is `001` and chapter 1 is `002`. |
| EPUB size limit | Confirmed: 100 MiB. |
| EPUB repair | Every imported EPUB goes through an automatic repair step before parsing. The reference's manual `make repair-ebook` (`repair-ebook.sh`) becomes a built-in step of the import pipeline, run off the UI thread. It does not replace the parser's strict chapter rules. |

## User Stories
- Given a DRM-free EPUB with explicit chapters, when I open Batch audio → **From EPUB** and choose the file, then I see the book title, its language, and a numbered chapter list with character counts, and the batch name is prefilled with the title.
- Given an imported book, when I type an intro and an outro and choose a voice, then the narration list shows intro → chapters → outro with each future MP3 name, and an empty intro or outro adds no track.
- Given an imported book, when I expand a chapter, then I read exactly the text that will be narrated, including the spoken heading in the voice's language, and I can untick chapters I do not want.
- Given a valid selection, when I select **Start book batch**, then one batch is created through the existing worker pipeline, I land on the Batches tab with the new batch expanded, and pause/resume/retry work as for any batch.
- Given an EPUB that is encrypted, malformed, or has no supported chapter markup, when I choose it, then I see a short reason and any previously imported book stays on screen.

## Functional Requirements
1. FR1 — `BatchAudio.razor` gets a third tab, **From EPUB**, between **New batch** and **Batches**. It uses the same `tab-list`/`tab`/`tab-panel` ARIA pattern and is opened by `?tab=epub`. The existing `?tab=batches` and `?batch=<id>` deep links keep opening the Batches tab.
2. FR2 — The From EPUB tab accepts exactly one `.epub` file through `InputFile` (`accept=".epub,application/epub+zip"`). It reads the file with `OpenReadStream(maxAllowedSize)` capped at the configured source limit (default 100 MiB), copies it to a randomly named temporary file, parses it, and deletes the temporary file on success, failure, or cancellation. The EPUB is never stored, and its client file name is used only as a display label.
3. FR3 — After the automatic repair step (FR17), parsing applies the reference `EpubChapterParser` rules unchanged:
   - EPUB 2/3 chapter discovery and chapter language resolution.
   - Mimetype marker check, archive entry-count, per-entry, and total-uncompressed limits, and XML character limits.
   - XML read with DTD processing ignored and no external resolution.
   - Encrypted content (`META-INF/encryption.xml`) rejected.

   Failures show the reference's public messages (`EpubParseException.PublicMessage`).
4. FR4 — A new pick replaces the current import only when parsing succeeds. On failure, the error is shown in a `role="alert"` region and the previous import, if any, is kept.
5. FR5 — After a successful import, a summary shows the title, package language, chapter count, and total characters. The batch name is prefilled from the title: `/`, `\`, and control characters are removed and the result is truncated to 80 characters. The user can edit it, and it is validated with `BatchRules.NameError`.
6. FR6 — The tab provides the same saved-voice selector as New batch, with the same Refresh voices and no-voices states. When the book's primary language subtag differs from the voice's `LanguageId`, a non-blocking warning names both languages.
7. FR7 — The tab provides optional **Intro** and **Outro** textareas, each limited to 5,000 characters with a live counter. Text is trimmed. An empty field produces no track. Intro and outro text is narrated as typed, with only whitespace normalized and no heading added.
8. FR8 — Chapters are listed in book order. Each row shows the chapter number, character count of its narrated text, future output name, and an include checkbox, which is checked by default. **Include all** and **Include none** actions are provided. Start requires at least one included chapter.
9. FR9 — Each chapter row has a Preview toggle button with `aria-expanded`/`aria-controls`. Expanding it shows the chapter's narrated text as paragraphs in a scrollable region. The text is exactly what will be uploaded, and it is rendered only while expanded. Before a voice is chosen, the preview shows the normalized paragraphs with a hint that the heading depends on the voice's language.
10. FR10 — Narrated chapter text is `"<Label> <Number>.\n\n<paragraphs joined by \n\n>\n"`.
    - The label is `Chapter` (en), `Capítulo` (pt), or `Kapitel` (sv).
    - `<Number>` is written as capitalized cardinal words for 0–999 in the voice language. Numbers outside that range are written as invariant digits.
    - Paragraphs pass through the ported `TtsTextNormalizer`, which keeps the reference en/pt behavior. Every language gets `***` removal and normalization of periods and whitespace.
    - `pt` and `sv` get the Portuguese dialogue-dash rules: the leading dash is removed, and an inline en or em dash surrounded by spaces becomes a comma and space, or just a space when the preceding text already ends in `.`, `!`, `?`, or `…`.
    - `en` keeps dialogue dashes.
11. FR11 — Narration order is the intro (if present), then included chapters in ascending chapter number, then the outro (if present). Uploaded display names are `intro.txt`, `chapter-NNN.txt` (the original chapter number, zero-padded to three digits), and `outro.txt`. Output names stay positional (`BatchRules.OutputName`), so with an intro, chapter 1 is `<Name> 002.mp3`. Each row shows its output name.
12. FR12 — Before Start, every track is validated with the existing `BatchRules` bounds: 1–200,000 characters, at most 1 MiB of UTF-8 per track, at most 200 tracks, and at most 10 MiB in total. Invalid included chapters are marked inline with the reason, and all messages are listed in the existing `.validation` alert. Start is disabled while any message remains, while no voice is chosen, or while the model is not ready.
13. FR13 — **Start book batch** calls the existing `StudioSession.CreateBatch(name, voiceId, uploads)` with one in-memory UTF-8 stream per track, in narration order. On success, the import draft is cleared, `Studio.SelectedVoice` is updated, the page switches to the Batches tab, and the new batch is expanded, as New batch does. On failure, the draft is kept and the session error is shown. The worker and `POST /batches` are unchanged.
14. FR14 — The import draft persists across tab switches and page navigation within the same circuit: parsed book, batch name, voice, intro, outro, inclusion set, and expanded previews. A **Clear** action discards it. A browser reload starts empty.
15. FR15 — The tab shows these states, consistent with New batch:
    - Empty, with the picker and a supported-format hint.
    - Parsing: "Reading EPUB…" with `aria-live`, and the inputs disabled.
    - Parse error.
    - Batch disabled (`TTS_BATCH_ENABLED=false`).
    - No voices.
    - Model not ready.
    - Uploading ("Uploading…").
16. FR16 — Logs record only the parse error category, whether a repair ran, chapter count, and total characters. They never record chapter text, intro or outro text, the EPUB title, or the client file name.
17. FR17 — Every imported EPUB passes through an automatic repair step before parsing. It ports `repair-ebook.sh` and runs inside the import's background task, so the user takes no manual action. The step:
    - Inspects the archive on every input. It is conforming when the first entry is `mimetype` and its content is exactly `application/epub+zip`, which is the parser's marker check.
    - Leaves a conforming archive unchanged and passes it straight to the parser.
    - Before rebuilding a non-conforming archive (marker missing, not first, or wrong value), rejects it with the script's messages if any entry path is unsafe (absolute, drive-letter, backslash, or `..`), any path is duplicated, any entry is a symbolic link, `mimetype` is a directory, or the archive breaks the entry-count or size limits.
    - Otherwise rebuilds the archive into a second temporary file. `mimetype` is written first, stored without compression, with the exact value `application/epub+zip`. Every other file entry follows in its original order with its original path and content, and any old `mimetype` entry is dropped.
    - Verifies the rebuilt archive: it opens as a ZIP, `mimetype` is the first and only marker, and the value is exact. It then parses the rebuilt archive.
    - Never modifies or stores the uploaded original. Both temporary files are deleted on success, failure, or cancellation.
    - After a successful import that needed repair, shows the non-blocking note "This EPUB's packaging was repaired automatically." A repair failure is shown as a parse error (FR4).

## Non-Functional Requirements
- **Memory and server load (Interactive Server):**
  - The EPUB is streamed to disk, never buffered whole in memory, following Learn guidance.
  - Parsed text kept in the circuit is capped by a configurable `MaxBookCharacters` (default 20,000,000). A larger book fails with a clear message.
  - Parsing runs off the circuit's synchronization context (`Task.Run`), so the parsing state renders and the circuit stays responsive.
- **Security:**
  - Archive paths are normalized and contained, and archive limits are enforced before entries are decompressed.
  - XML is read with `DtdProcessing.Ignore`, `XmlResolver = null`, `MaxCharactersFromEntities = 0`, and `MaxCharactersInDocument`.
  - No client-supplied path or name reaches the file system.
  - Razor encoding covers every displayed title, label, and preview string.
- **Privacy and copyright:** books and extracted text are never committed, never copied into tests, and never logged. Tests use synthetic EPUBs only.
- **SignalR:** intro and outro bindings stay at 5,000 characters, the same bound as `GenerateAudio.razor`, well under the default 32 KB `MaximumReceiveMessageSize`. No hub option change is needed.
- **Design (SOLID):**
  - Parsing, normalization, number words, the track builder, and the temporary-file import each live in a focused class behind a small interface, registered in `Program.cs`.
  - Pure naming, language, and limit rules live in a static `EpubBookRules`, following the `BatchRules` convention.
  - The Razor component holds only workflow state.
- **No new dependencies:** only BCL APIs (`System.IO.Compression`, `System.Xml.Linq`) and existing ASP.NET Core APIs are used. No NuGet package, container, or worker change is added.
- **Testability:** the parser takes a seekable `Stream`, so tests build EPUBs in memory without temporary directories.

## Out of Scope
- Any change to the Python worker, `POST /batches`, batch limits, or MP3 settings.
- Fallback chapter discovery (TOC-only or spine-document chapters) for books without explicit chapter markup.
- Editing chapter text, reordering chapters, or merging chapters.
- Splitting chapters over 200,000 characters or 1 MiB into multiple tracks.
- Repairs beyond the `repair-ebook.sh` scope: converting EPUB 2 navigation, adding chapter semantics, fixing malformed XHTML, or fixing corrupt ZIP data. Also out: DRM removal, fixed-layout EPUBs, MOBI/AZW/PDF, OCR, and translation.
- Downloading or keeping the repaired EPUB. Only its parsed text is used.
- Cover art, ID3 tags, or using the book title or author anywhere other than the batch name prefill.
- Persisting the import draft across browser reloads or server restarts, and storing EPUB files on the server.
- Changing the reference `book-notes-ia` project.

## Open Questions
- All discovery questions are resolved. See [Discovery Decisions](#discovery-decisions).
- ⚠️ TODO (implementation): the Portuguese inline-dash rule also turns a Swedish parenthetical *tankstreck* in narration (`Han kom – till slut – hem`) into commas. That reads naturally aloud and matches the existing Portuguese behavior. Revisit only if listening tests show a problem.
