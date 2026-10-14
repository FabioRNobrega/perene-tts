# Plan: EPUB Book Batch Import

## Table of Contents
- [Summary](#summary)
- [Technical Approach](#technical-approach)
  - [Where parsing lives](#where-parsing-lives)
  - [Porting the reference parser](#porting-the-reference-parser)
  - [Import service and temporary file](#import-service-and-temporary-file)
  - [Automatic EPUB repair step](#automatic-epub-repair-step)
  - [Narrated text and track building](#narrated-text-and-track-building)
  - [Circuit-scoped import draft](#circuit-scoped-import-draft)
  - [Web: Batch audio tabs](#web-batch-audio-tabs)
  - [Web: From EPUB panel](#web-from-epub-panel)
  - [Starting the batch](#starting-the-batch)
  - [Design principles applied](#design-principles-applied)
- [Component Breakdown](#component-breakdown)
- [Dependencies](#dependencies)
- [External / Vendor Documentation Evidence](#external--vendor-documentation-evidence)
- [Flow](#flow)
- [Risk Assessment](#risk-assessment)

## Summary
Port the strict EPUB chapter parser, TTS normalizer, and number-words converter from `book-notes-ia/services/EbookParseService.Api` into `WebApp` as focused DI services. Also turn its manual `repair-ebook.sh` into an automatic repair step that runs on every imported EPUB before parsing. Add a **From EPUB** tab to `BatchAudio.razor` for importing a book, typing an intro and outro, previewing and selecting chapters, and starting a single batch. The tab builds in-memory `.txt` uploads and sends them through the existing `StudioSession.CreateBatch` → `TtsClient.CreateBatchAsync` → worker `POST /batches` pipeline, which stays unchanged.

## Technical Approach

### Where parsing lives
The parser runs in the .NET web host, not in the Python worker or a new container:
- The reference is already .NET 10 and uses only BCL APIs, so it moves into `WebApp` without new packages.
- The worker owns the model lock and batch orchestration (AGENTS.md). Parsing is a pre-batch, CPU-light step that produces exactly the `.txt` input the worker already validates.
- Adding the reference API as a fourth Compose service would mean an extra container, a shared upload volume, and path-based input. The repo constraints ("keep this foundation local and simple", "never accept client-supplied filesystem paths") argue against that.

### Porting the reference parser
New namespace `PereneTts.Services.Epub` under `WebApp/Services/Epub/`. Each ported file starts with a one-line comment naming its reference source. Changes from the reference:

| Reference | Perene TTS port | Change |
| --- | --- | --- |
| `IEpubChapterParser.ParseAsync(string fileName, …)` | `ParseAsync(Stream epub, CancellationToken)` | Takes a seekable stream. Removes `ResolveSourcePath`, symlink, and input-folder logic, plus the `ZipFile.OpenRead(path)` call (replaced by `new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true)`). `SourceFileName` leaves `ParsedEpubBook`. All discovery, `ValidateArchive`, `BuildEntryMap`, `CreateXmlReaderSettings`, path normalization, and exception mapping are kept verbatim. |
| `EpubParserOptions` | `EpubParserOptions` | Drops `InputDirectory`/`OutputDirectory`. Keeps the archive and XML limits with the same defaults. Adds `MaxBookCharacters` (20,000,000), which the parser checks after extraction. Bound from the `EpubParser` configuration section. |
| `EpubParseException`, `EpubParseErrorKind` | same | `OutputConflict` is removed because nothing is written to disk. |
| `ITtsTextNormalizer.Normalize(ParsedEpubBook, TtsTextOptions)` | `NormalizeParagraphs(IReadOnlyList<string>, string languageId)` | Works per chapter so the preview can be recomputed when the voice changes. The `language == "pt"` guard around `LeadingDialogueDash`/`InlineDialogueDash` becomes `language is "pt" or "sv"`, because Swedish uses the same dialogue convention (see the evidence section). The regexes and the `HasTerminalPunctuation` logic are unchanged. |
| `repair-ebook.sh` (manual `make repair-ebook`) | `EpubArchiveRepairer` | A C# port that runs automatically on every import before parsing. See [Automatic EPUB repair step](#automatic-epub-repair-step). |
| `INumberToWordsConverter` | same signature | Adds `sv`: `noll`…`nitton`, the tens `tjugo`…`nittio`, and `hundra` with an `ett`/`två`… prefix, written as one word (for example `etthundratjugofyra`). |
| `ChapterOutputWriter.Render` | `BookTrackBuilder` | Rendering only (heading + blank line + paragraphs). The staging and atomic directory publication are not ported. |
| `EpubController` | none | No HTTP endpoint. The Razor component calls the services in-process. |
| `Models/ParsedChapter`, `ParsedEpubBook` | `WebApp/Models/EpubBook.cs` | `ParsedEpubBook(Title, Language, Chapters)`, `ParsedChapter(Number, Paragraphs)`. |

Heading labels move from the normalizer into a small lookup in `EpubBookRules`: `en` → `Chapter`, `pt` → `Capítulo`, `sv` → `Kapitel`. Capitalization of the first word follows the reference (`Twenty-four`, `Vinte e quatro`, `Tjugofyra`).

### Import service and temporary file
`EpubImportService : IEpubImportService` is a singleton with this method:

```csharp
Task<ParsedEpubBook> ImportAsync(IBrowserFile file, CancellationToken cancellation);
```

It works in these steps:
1. Create `Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())` as a `FileStream` with `FileMode.CreateNew`, `FileShare.None`, and `FileOptions.Asynchronous | FileOptions.DeleteOnClose`.
2. Copy `file.OpenReadStream(options.MaxSourceFileBytes, cancellation)` into it. `IOException` maps to `EpubParseException(InvalidRequest, "The EPUB exceeds 100 MiB.")`.
3. Seek to 0 and run one background task with `Task.Run`, covering both the repair and the parse:
   ```csharp
   await Task.Run(async () =>
   {
       await using var prepared = await repairer.PrepareAsync(temp, cancellation); // FR17
       return (await parser.ParseAsync(prepared.Stream, cancellation), prepared.Repaired);
   }, cancellation);
   ```
   Repair and parse are synchronous CPU/IO work. Running them off the circuit's sync context keeps the "Reading EPUB…" state rendered and the circuit responsive.
4. Dispose the streams, which deletes both temporary files even on exceptions or cancellation.

The result is `EpubImportResult(ParsedEpubBook Book, bool Repaired)`. The client file name is never used to build a path. The service logs only `EpubParseErrorKind`, `Repaired`, chapter count, and character totals (FR16).

### Automatic EPUB repair step
`EpubArchiveRepairer : IEpubArchiveRepairer` is a singleton that depends on `IOptions<EpubParserOptions>`. It replaces the reference's manual `repair-ebook.sh`, which used `unzip`/`zip`, extracted to a directory, and wrote `<name>-fixed.epub` beside the source. Here the repair is an in-process, stream-to-stream step that every import runs before parsing (FR17). No shell tools are needed, and nothing is written next to user files.

```csharp
Task<PreparedEpub> PrepareAsync(FileStream source, CancellationToken cancellation);
// PreparedEpub(Stream Stream, bool Repaired) : IAsyncDisposable. It owns and deletes the rebuilt temp file, if any.
```

The algorithm mirrors the script and checks the ZIP central directory before reading any content:
1. **Inspect** (every input): open `new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true)`. `InvalidDataException` maps to "The source is not a valid ZIP archive." The archive is *conforming* when `Entries[0].FullName == "mimetype"` and its content, read with the reference's 64-character `ReadSmallText`, equals `application/epub+zip`. This is exactly the parser's `ValidateArchive` marker rule. A conforming archive returns `PreparedEpub(source, Repaired: false)` with no copy.
2. **Safety checks**, run before rebuilding a non-conforming archive and taken from `repair-ebook.sh`:
   - Reject entry paths that are absolute, contain `\`, start with a drive letter, or have a `..` segment ("unsafe entry path").
   - Reject duplicate `FullName`s ("duplicate entry paths").
   - Reject symbolic links, detected as Unix mode `(ExternalAttributes >> 16) & 0xF000 == 0xA000` ("symbolic links").
   - Reject a `mimetype/` directory entry ("invalid mimetype directory").
   - Enforce the parser's `MaxArchiveEntryCount`, `MaxEntryBytes`, and `MaxTotalUncompressedBytes` using metadata `Length`, before any entry is opened.

   Every failure is `EpubParseException(InvalidEpub, <script message>)`.
3. **Rebuild** into a second temporary file (random name, `DeleteOnClose`) with `new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true)`:
   - Write `CreateEntry("mimetype", CompressionLevel.NoCompression)` with the ASCII bytes `application/epub+zip` and no BOM.
   - Then copy each remaining file entry in its original order. Skip directory entries and any old `mimetype`. Use `CreateEntry(entry.FullName, CompressionLevel.Optimal)`, copy stream to stream with a byte counter that stops at `MaxEntryBytes`, and keep `LastWriteTime`.
   - Create mode streams entries to the seekable file; it does not use the `Update` mode that buffers in memory.
4. **Verify** as the script does: reopen the rebuilt file in Read mode. Exactly one `mimetype` entry must exist, it must be at index 0, and its value must be exact. A failure gives "The repaired EPUB failed its integrity check." Return `PreparedEpub(rebuilt, Repaired: true)`.

The step never touches the content documents, the NCX/nav, or the encryption check. Those stay with the parser, so strictness (FR3) is unchanged. A ZIP that cannot be opened at all is still rejected. The parser's own marker check remains as defense in depth.

### Narrated text and track building
`BookTrackBuilder : IBookTrackBuilder` depends on `ITtsTextNormalizer` and `INumberToWordsConverter`:

```csharp
string ChapterText(ParsedChapter chapter, string? languageId);            // preview + upload text
IReadOnlyList<BookTrack> Build(ParsedEpubBook book, BookSelection selection, string? languageId);
```

- `BookSelection(string Intro, string Outro, IReadOnlySet<int> Excluded)`.
- `BookTrack(string DisplayName, string Text, int Characters, int Bytes, int? ChapterNumber)`.
- With no `languageId` (no voice chosen), `ChapterText` returns only the normalized paragraphs, using language-neutral rules. The preview shows this together with the heading hint (FR9).
- The intro and outro collapse runs of horizontal whitespace and trim. A blank intro or outro produces no track (FR7).
- Ordering follows FR11, and display names come from `EpubBookRules.TrackFileName`.

`EpubBookRules` is a static class with pure rules, unit-tested like `BatchRules`:
- `DefaultBatchName(string title)`: removes `/`, `\`, and control characters, collapses whitespace, and truncates to `BatchRules.MaxNameLength`.
- `ChapterLabel(string languageId)`.
- `TrackFileName(kind, number)` → `intro.txt` / `chapter-007.txt` / `outro.txt`.
- `PrimaryLanguage(string bcp47)` → `pt` for `pt-BR`, and `LanguageMismatch(bookLanguage, voiceLanguage)`.
- `TrackErrors(IReadOnlyList<BookTrack>)` reuses `BatchRules.FileError(name, bytes, characters)` and `BatchRules.SelectionError(count, totalBytes)`, and adds "Include at least one chapter."
- `const int MaxIntroCharacters = 5000`.

### Circuit-scoped import draft
`EpubImportDraft` is a plain scoped service, which in Blazor Server means one instance per circuit. It holds:
- `ParsedEpubBook? Book` and `string SourceLabel`
- `Name`, `VoiceId`, `Intro`, `Outro`
- `HashSet<int> Excluded` and `HashSet<int> Expanded`
- `Clear()`

This lets the import survive the tab switch: the panel is unmounted when another tab renders, which is the existing `if/else` panel pattern. It also survives navigation within the circuit, in the same way `StudioSession` keeps `SpeechText` and `SelectedVoice`. No conversion state lives here. Batch progress still comes from the worker (AGENTS.md).

### Web: Batch audio tabs
In `BatchAudio.razor`:
- Replace `bool showBatches` with `private enum Tab { New, Epub, Batches }` and `Tab tab`.
- Add the middle tab button `id="tab-epub"`, `aria-controls="panel-epub"`, with icon `bi-book` and the label **From EPUB**.
- `OnParametersSetAsync` maps `?tab=epub` → `Tab.Epub`, `?tab=batches` → `Tab.Batches`, and `?batch=<id>` → `Tab.Batches` with that batch expanded.
- The empty-state "New batch" button keeps targeting `Tab.New`.
- The page renders `<EpubBatchPanel OnCreated="BatchCreated" />` in `panel-epub`.
- `BatchCreated(BatchSummary created)` holds the success branch that `StartBatch` already has: reset `pageNumber`, switch to Batches, `expanded.Add`, then `RefreshDetails`. `StartBatch` calls it too, so the code is not duplicated.

### Web: From EPUB panel
`WebApp/Components/EpubBatchPanel.razor` inherits `StudioComponent` and injects `IEpubImportService`, `IBookTrackBuilder`, and `EpubImportDraft`. It reuses the existing `workspace` / `card` / `support-cards` layout and the existing classes `label-row`, `hint`, `upload`, `picker`, `file-list`, `file-row`, `track-number`, `job-info`, `job-actions`, `validation`, `field-error`, `primary`, `secondary`, and `quiet`.

The main card, **Import a book**, contains:
1. When batch audio is disabled, the same `TTS_BATCH_ENABLED=false` notice as New batch.
2. The picker block: `InputFile id="epub-file" accept=".epub,application/epub+zip"`, plus the hidden-input/visible-label pattern `button secondary picker-button` with the label "Choose .epub". Below it is the hint "One DRM-free EPUB 2 or 3 with explicit chapters · up to 100 MiB".
3. A status region (`role="status" aria-live="polite"`) that shows "Reading EPUB…" while parsing.
4. A parse error in `role="alert"`.
5. After import, a summary row (title · language · N chapters · characters) and a **Clear** quiet button. When `Repaired` is true, a `hint` reads "This EPUB's packaging was repaired automatically." The draft stores `Repaired` alongside `Book`.
6. Batch name: the same input, counter, and hint as New batch, bound to `Draft.Name`.
7. Saved voice: the same select and Refresh voices control, plus the language-mismatch hint (FR6).
8. **Intro** and **Outro** textareas: `maxlength="5000"`, `rows="4"`, `@bind:event="oninput"`, and a counter in the `label-row`. The hint reads "Read before chapter 1 · leave empty to skip".
9. The narration list (`ol.file-list`):
   - Intro and outro rows appear only when they are non-empty and show no preview toggle.
   - Each chapter row is a `li.file-row` with:
     - A checkbox (`aria-label="Include chapter N"`).
     - The `track-number`, which is the positional output number, or "—" when the chapter is excluded.
     - A `job-info` with "Chapter N", the characters, the "→ output name" text, and any `field-error`.
     - A `job-actions` with the preview toggle `button secondary icon-button`. It uses `bi-eye` / `bi-eye-slash` and carries `aria-expanded` and `aria-controls="chapter-preview-N"`.
   - An expanded row renders `<div id="chapter-preview-N" class="chapter-preview" tabindex="0">` with one `<p>` per paragraph.
   - **Include all** / **Include none** quiet buttons sit above the list.
10. The `.validation` alert list and a primary **Start book batch** button with `bi-play-circle`. The button label changes to "Uploading…" while the upload runs, followed by the model-not-ready hint.

The side cards are:
- **Narration order**: "Your intro is read first, then each included chapter in book order, then your outro. Each becomes one MP3."
- **Supported books**: EPUB 2/3, DRM-free, and chapters marked as chapters or numbered in the table of contents. Other books are rejected rather than guessed.

Styles go in the authored `WebApp/wwwroot/app.css` and add only a few rules:
- `.chapter-preview` uses the existing surface/border tokens, `max-height: 22rem`, `overflow-y: auto`, and readable line length and paragraph spacing.
- A `.include-toggle` alignment rule.
- The tab row can now hold three tabs. Under the existing `@media` block, the tab list scrolls horizontally instead of wrapping at phone width.

The UI uses the existing theme tokens for dark and paper-light themes and shows a visible focus style on the checkbox and toggle buttons.

### Starting the batch
`StartBookBatch()` works as follows:
1. Build the tracks with `IBookTrackBuilder.Build`. If `EpubBookRules.TrackErrors` is non-empty, stop.
2. Create `uploads = tracks.Select(t => new BatchUpload(t.DisplayName, new MemoryStream(Encoding.UTF8.GetBytes(t.Text), writable: false)))`. The text is already validated to be at most 10 MiB in total, and it is already in memory as strings. `LazyBrowserFileStream` is not needed because nothing comes from the browser.
3. `await Studio.CreateBatch(Draft.Name.Trim(), Draft.VoiceId, uploads)`, then dispose the streams in `finally`.
4. On success, set `Studio.SelectedVoice`, call `Draft.Clear()`, and `await OnCreated.InvokeAsync(created)`.

The worker's `batch_project.build_source` re-validates every track: UTF-8, trimmed length, chunkability, and the `.txt` suffix of the display name.

### Design principles applied
- **Single responsibility:** the repairer fixes only the archive packaging, the parser discovers chapters, the normalizer rewrites paragraphs, the converter writes numbers as words, the track builder composes tracks, the import service handles temporary-file I/O, the draft holds circuit state, and the component handles workflow and rendering.
- **Dependency inversion:** the component depends on `IEpubImportService` and `IBookTrackBuilder`. Tests construct the concrete classes directly with fakes or synthetic streams.
- **Open/closed:** adding a language means adding a converter branch, a normalizer branch, and a label entry. Batch upload code does not change.
- **Existing patterns extended:** `BatchRules`-style static rules, `StudioSession.CreateBatch`, the tab shell, `StudioComponent`, the typed `BatchUpload` contract, and the xUnit tests in `WebApp.Tests`.

## Component Breakdown

**Existing files to modify:**
- `WebApp/Components/Pages/BatchAudio.razor`: change the tab bool to an enum, add the **From EPUB** tab and the `?tab=epub` mapping, render `EpubBatchPanel`, and extract `BatchCreated` from `StartBatch`.
- `WebApp/Program.cs`: register `IEpubArchiveRepairer`, `IEpubChapterParser`, `ITtsTextNormalizer`, `INumberToWordsConverter`, `IBookTrackBuilder`, and `IEpubImportService` as singletons. Register `EpubImportDraft` as scoped. Bind `EpubParserOptions` from the `EpubParser` section with the reference's positive-limit validation and `ValidateOnStart`.
- `WebApp/wwwroot/app.css`: add `.chapter-preview` and `.include-toggle`, and make the tab row scroll at phone width.
- `WebApp/Components/_Imports.razor`: add `@using PereneTts.Services.Epub`.
- `README.md`: add an "Import an EPUB book" workflow section covering limits, strictness, privacy, and intro/outro.
- `AGENTS.md`: after implementation, update it through the init-agent skill, covering the repository map, architecture (EPUB import), and conventions.

**New files to create:**
- `WebApp/Services/Epub/IEpubChapterParser.cs` and `EpubChapterParser.cs`: the stream-based port of the strict parser.
- `WebApp/Services/Epub/EpubParseException.cs`: the error kinds and public messages.
- `WebApp/Services/Epub/EpubParserOptions.cs`: the archive, XML, source, and book-character limits.
- `WebApp/Services/Epub/ITtsTextNormalizer.cs` and `TtsTextNormalizer.cs`: paragraph normalization for en/pt/sv.
- `WebApp/Services/Epub/INumberToWordsConverter.cs` and `NumberToWordsConverter.cs`: cardinal words 0–999 for en/pt/sv.
- `WebApp/Services/Epub/IBookTrackBuilder.cs` and `BookTrackBuilder.cs`: narrated text and ordered tracks.
- `WebApp/Services/Epub/IEpubArchiveRepairer.cs`, `EpubArchiveRepairer.cs`, and `PreparedEpub.cs`: the automatic repair step, a stream-based C# port of `repair-ebook.sh` (FR17).
- `WebApp/Services/Epub/IEpubImportService.cs` and `EpubImportService.cs`: browser file → temporary file → repair → parse, in one background task.
- `WebApp/Services/Epub/EpubImportDraft.cs`: the circuit-scoped draft.
- `WebApp/Models/EpubBook.cs`: `ParsedEpubBook`, `ParsedChapter`, `BookSelection`, and `BookTrack` records.
- `WebApp/Models/EpubBookRules.cs`: pure naming, label, language, and validation rules.
- `WebApp/Components/EpubBatchPanel.razor`: the From EPUB tab UI.
- `WebApp.Tests/Epub/SyntheticEpubBuilder.cs`: an adapted copy of the reference builder that returns a `MemoryStream` (no temporary directories).
- `WebApp.Tests/Epub/EpubChapterParserTests.cs`: ported discovery and rejection tests. The input-folder and symlink tests are dropped because they no longer apply.
- `WebApp.Tests/Epub/TtsTextNormalizerTests.cs` and `NumberToWordsConverterTests.cs`: the ported tests plus Swedish cases.
- `WebApp.Tests/Epub/EpubArchiveRepairerTests.cs`: covers a conforming no-op, a missing, reordered, or wrong marker being rebuilt, each rejection rule, and temporary-file deletion.
- `WebApp.Tests/Epub/BookTrackBuilderTests.cs`, `EpubBookRulesTests.cs`, and `EpubImportServiceTests.cs`. The import service tests use a fake `IBrowserFile` to cover the size limit, temporary-file deletion, and error mapping.

## Dependencies
- .NET 10 SDK/runtime images that the Dockerfile already uses. The BCL provides `System.IO.Compression` and `System.Xml.Linq`, and the shared framework provides `Microsoft.Extensions.Options`. No new NuGet package.
- A writable `Path.GetTempPath()` in the web container. `/tmp` must be writable by the non-root runtime user, and the SDK watch container needs the same.
- A running worker with `TTS_BATCH_ENABLED` not set to `false` and a saved voice, as for any batch.
- The reference source at `../book-notes-ia/services/EbookParseService.Api`, read-only while porting.

## External / Vendor Documentation Evidence
- [ASP.NET Core Blazor file uploads (.NET 10)](https://learn.microsoft.com/aspnet/core/blazor/file-uploads?view=aspnetcore-10.0). It supports these choices:
  - Pass an explicit `maxAllowedSize` to `OpenReadStream`, because the default is 500 KB. The limit here is the 100 MiB source limit.
  - Do not use the untrusted `IBrowserFile.Size` as the limit.
  - With server interactivity, file data streams over SignalR. Outside small files, do not copy it into a `MemoryStream`; copy it straight to a file on disk. That is why the EPUB goes to a temporary file.
  - Never use the client file name for storage. Use `Path.GetRandomFileName()`, and rely on Razor encoding for display.
  - Cancel with a `CancellationToken` passed to `OpenReadStream`.
- [IBrowserFile.OpenReadStream](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.components.forms.ibrowserfile.openreadstream?view=aspnetcore-10.0): it throws `IOException` when the file exceeds `maxAllowedSize`. That exception maps to a size message.
- [Best practices for working with ZIP and TAR archives in .NET](https://learn.microsoft.com/dotnet/standard/io/zip-tar-best-practices). It supports these choices:
  - `ZipArchive` does not limit uncompressed size or entry count, so the app enforces per-entry, total, and count limits. The reference `ValidateArchive` already does this using metadata `Length` before opening entries.
  - Avoid `ZipArchiveMode.Read` over unseekable untrusted streams, because the runtime buffers the whole archive in memory. That is a second reason to parse from a seekable temporary `FileStream`.
  - Treat entry names as untrusted. Path normalization and containment are retained from the reference.
  - Always dispose the archive.
- [CA3075: Insecure DTD processing](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca3075) and [XmlReader.Create security guidance](https://learn.microsoft.com/dotnet/api/system.xml.xmlreader.create?view=net-10.0): for untrusted XML, set `DtdProcessing` to `Prohibit` or `Ignore` and `XmlResolver = null`. The reference uses `Ignore`, because XHTML content documents carry DOCTYPEs that `Prohibit` would reject, together with `MaxCharactersFromEntities = 0` and `MaxCharactersInDocument`. This is retained as is.
- [ZipArchive best practices: memory and entry metadata](https://learn.microsoft.com/dotnet/standard/io/zip-tar-best-practices#memory-and-performance-considerations). These support the repair step:
  - Reading `Length`/`FullName`/`ExternalAttributes` does not decompress, so the safety checks and limits run before any content is read.
  - `ExternalAttributes` and entry names are untrusted metadata to validate. Unix link bits are rejected.
  - `Update` mode buffers entries in memory, so the rebuild uses `Read` → `Create` between two seekable temporary files instead.
- **Swedish dialogue convention (web sources; not vendor documentation):**
  - Språkrådet's Q&A at the Institute for Language and Folklore says lines of dialogue are common in news and fiction. Each new line starts on a new indented line with a new dash, and no quotation marks are used (`– Kommer du med?` / `– Nej, jag måste först avsluta det här jobbet.`). The dash is also called pratminus, replikstreck, talstreck, or anföringsstreck. Source: [frageladan.isof.se/faqs/27075](https://frageladan.isof.se/faqs/27075).
  - Published examples show the speaker tag after the line, inside the same dashed paragraph: `– Jaha. Vad gör vi nu då? sa Irene till Tommy.` (Tursten, *Guldkalven*, 2005, quoted in [W3C www-international, 2016](https://lists.w3.org/Archives/Public/www-international/2016AprJun/0108.html)). The same post shows that some Swedish books use quotation marks instead (`”Visst”, svarade Tommy`). Those are unaffected by the dash rules.
  - Further background: [Elon Swedish punctuation](https://elon.io/grammar/swedish/spelling/punctuation) and [Språkbruk: Tankstreck och bindestreck](https://sprakbruk.fi/artiklar/tankstreck-och-bindestreck/).
  - Conclusion: Swedish dialogue has the same structure as Portuguese *travessão* dialogue: a leading dash plus a space, with the tag or narration after the line. So the reference Portuguese rules apply to `sv` unchanged. This makes Chatterbox read `Jaha. Vad gör vi nu då? sa Irene till Tommy.` instead of voicing a leading dash.
- **Repo constraint versus reference architecture:** the reference runs as a separate API that reads from a private input folder. This repo forbids client-supplied filesystem paths and keeps the stack to web + worker, so the parser runs in-process in `WebApp` with stream input. The worker remains the only owner of batch state.
- **SignalR message size:** Learn's [large form payloads guidance](https://learn.microsoft.com/aspnet/core/blazor/forms/troubleshoot?view=aspnetcore-10.0#large-form-payloads-and-the-signalr-message-size-limit) applies to bound textareas. Intro and outro stay at 5,000 characters, the same bound as `GenerateAudio.razor`, so `HubOptions.MaximumReceiveMessageSize` is unchanged.

## Flow

```mermaid
sequenceDiagram
    actor User
    participant Panel as EpubBatchPanel.razor
    participant Import as EpubImportService
    participant Repair as EpubArchiveRepairer
    participant Parser as EpubChapterParser
    participant Builder as BookTrackBuilder
    participant Session as StudioSession
    participant Client as TtsClient
    participant Worker as Worker POST /batches

    User->>Panel: Choose .epub (InputFile)
    Panel->>Import: ImportAsync(file, token)
    Import->>Import: OpenReadStream(100 MiB) → temp FileStream (DeleteOnClose)
    Note over Import,Parser: one background Task.Run
    Import->>Repair: PrepareAsync(temp stream)
    alt mimetype marker conforming
        Repair-->>Import: PreparedEpub(source, Repaired=false)
    else marker missing / reordered / wrong
        Repair->>Repair: safety checks → rebuild to 2nd temp (mimetype first, stored) → verify
        Repair-->>Import: PreparedEpub(rebuilt, Repaired=true)
    end
    Import->>Parser: ParseAsync(prepared stream)
    Parser-->>Import: ParsedEpubBook(title, language, chapters)
    Import-->>Panel: EpubImportResult(book, repaired) (temp files deleted)
    Panel->>Panel: Draft.Book = book; Name = DefaultBatchName(title); show repaired note
    User->>Panel: Type intro/outro, choose voice, toggle chapters, expand preview
    Panel->>Builder: ChapterText(chapter, voice language)
    Builder-->>Panel: "Chapter Twenty-four.\n\n…"
    User->>Panel: Start book batch
    Panel->>Builder: Build(book, selection, language)
    Builder-->>Panel: [intro.txt, chapter-001.txt, …, outro.txt]
    Panel->>Panel: EpubBookRules.TrackErrors(tracks) == []
    Panel->>Session: CreateBatch(name, voiceId, uploads)
    Session->>Client: CreateBatchAsync(multipart, ordered)
    Client->>Worker: POST /batches
    Worker-->>Client: 202 queued BatchSummary
    Client-->>Panel: created
    Panel->>Panel: Draft.Clear(); OnCreated(created) → Batches tab, row expanded
```

## Risk Assessment

| Risk | Evidence | Mitigation |
| --- | --- | --- |
| Strict rules reject many commercial EPUBs | The reference README excludes arbitrary named EPUB 2 labels and books without explicit structure. The user chose strict. | Show a clear message that names what is required. The side card lists supported books. Fallback is recorded as out of scope for a later spec. |
| A 100 MiB upload over SignalR is slow and holds the circuit | Learn: server interactivity streams file data over SignalR. | Stream to disk, not memory. Show "Reading EPUB…" with inputs disabled. Cancel through the panel's lifetime token on dispose. Keep the limit configurable. The limit is an open question. |
| Synchronous parse blocks the circuit | The reference `ParseAsync` is synchronous (`Task.FromResult`). | Run it with `Task.Run` in `EpubImportService`. |
| Memory used by retained book text | Up to 500 MiB uncompressed is allowed by the archive limits. | `MaxBookCharacters` caps the retained text. The batch's own 10 MiB limit is shown in validation. |
| Port drifts from the reference behavior | 961-line parser with edge cases. | Copy the logic verbatim except the input boundary. Port the reference parser tests and synthetic builder. Name the source in each file header. |
| The temporary file leaks on crash or cancellation | Temporary files in the container's `/tmp`. | `FileOptions.DeleteOnClose`, a random name, and `using` disposal. A test asserts deletion. |
| Breaking existing deep links when the tab bool becomes an enum | `?tab=batches` and `?batch=` come from the sidebar and My creations. | Explicit mapping in `OnParametersSetAsync`. Manual checks for both links. |
| Wrong-language narration | The EPUB language may differ from the voice language. | Non-blocking mismatch warning. Normalization and heading always follow the voice language, the same way the worker reads every track. |
| Repair rebuild used as a ZIP-bomb or path-traversal vector | The script rejected unsafe, duplicate, and symlink entries before extracting. | The same checks plus the parser's count, entry, and total limits run on central-directory metadata before any entry is opened. The copy is stream to stream with a byte counter. Nothing is extracted to a directory, so no entry path ever reaches the file system. |
| Rebuilding a large EPUB doubles temporary disk use and import time | Up to 100 MiB source and 500 MiB uncompressed. | Rebuild only when the marker check fails, which is a cheap inspection on every input. Both files use `DeleteOnClose`. The cost is bounded by the existing limits. |
| The repair hides real corruption | The script only fixes the marker and entry order. | The repair scope is limited to the marker. Corrupt ZIP data, XML, or chapter structure still fails in the parser with its strict messages. |
| Swedish parenthetical dashes become commas | The Portuguese inline-dash rule applies to any spaced dash. | This reads naturally aloud and matches the existing Portuguese behavior. It is recorded as an implementation note in Requirements. |
| Leaking book content | Copyright and privacy (reference README). | No text, title, or file name in logs. Synthetic fixtures only. The EPUB is never stored. |
