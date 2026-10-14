# Validation: EPUB Chapter Discovery Relaxations and No-Chapter Fallback

## Table of Contents
- [Acceptance Criteria](#acceptance-criteria)
- [Test Cases](#test-cases)
- [Manual Verification](#manual-verification)
- [Definition of Done](#definition-of-done)
- [Rollback Plan](#rollback-plan)

## Acceptance Criteria

| Requirement | Acceptance Criterion |
| --- | --- |
| FR1 | NCX labels `Chapter 1: The Journeyman`, `chapter 2 – Title`, `Capítulo 3: O Fim`, and `Kapitel 4` parse as chapters 1–4 when confirmed. `Chapter one` and `Chapters 1-3` are not chapters. Existing `Chapter 1`, `1`, and `1. Start` fixtures still pass. |
| FR2 | An EPUB 3 nav label `Chapter 7` (confirmed by `<h1>7.</h1>`) becomes a chapter. A bare `7` keeps working. |
| FR3 | A Calibre-split fixture with labels `Chapter N: Title`, no numeric headings, and `<section epub:type="bodymatter chapter">` imports all chapters. The same fixture without `epub:type` and without headings fails with `UnsupportedStructure`. |
| FR4 | An EPUB 3 nav with whole-document links to Cover, Title, Copyright, and Chapter 1–3 imports exactly 3 chapters. A link with `?query` is still rejected. |
| FR5 | NCX targets `text/part0006_split_000.html#<body id>` are treated as whole documents, and the chapter text includes the following `split_001` file. |
| FR6 | In a fixture where the last chapter is followed by Epilogue and Acknowledgments TOC entries, the last chapter contains neither the Epilogue nor the Acknowledgments text. A chapter never contains text from a later TOC-targeted document. |
| FR7 | Entries labelled `Introduction`, `Epílogo`, or `Förord`, or typed `introduction`/`epilogue`, become sections. `Acknowledgments`, `Copyright`, and `About the Author` do not. An unnumbered `epub:type="chapter"` entry labelled `Opening` stays a chapter, as in the existing test. |
| FR8 | Sections appear in book order and are unticked by default. A section with no text is omitted. A TOC with only sections fails with `UnsupportedStructure`. |
| FR9 | `<h4>Footnotes</h4><p epub:type="footnote">…</p>` at a chapter end is absent from the chapter text. An untyped `<p>Notes on the text</p>` is kept. |
| FR10 | A chapter whose first paragraph is `Chapter 1` (or `1.`) no longer contains it. A first paragraph `1984 was a year…` is kept. A section whose first paragraph equals its title drops it. |
| FR11 | The page-list fixture (NCX `1`, `2` → `#page-N` divs, no headings) returns a fallback offer with the strict reason. An encrypted fixture and a non-ZIP file still throw without an offer. An image-only fixture throws "…This book has no readable text." |
| FR12 | 90,000 characters at a 30,000 target gives 3 parts of balanced size. 30,001 characters gives 2 parts. A single 40,000-character paragraph stays whole in one part. Parts are named `part-001.txt`… |
| FR13 | After a failed strict import, the alert shows the reason, the FR13 sentence with N, and a **Split into parts** button. Selecting it loads the parts. Choosing another file or selecting Clear removes the offer. Before the button is selected, a previously imported book is still shown unchanged. |
| FR14 | A parts book shows "N parts · no chapters found", rows "Part N" all ticked, no heading hint, and "Include at least one part." after Include none. |
| FR15 | *Press Reset*-style fixture: the summary reads "9 chapters · 2 sections". Including only a section enables Start. Including nothing shows "Include at least one chapter." |
| FR16 | With an English voice, a section track starts `Introduction.\n\n`. A title `What Now?` stays `What Now?` (no extra period). A part track starts directly with its first paragraph. |
| FR17 | Upload names for intro + Introduction + chapters 1–2 + Epilogue + outro are `intro.txt`, `introduction.txt`, `chapter-001.txt`, `chapter-002.txt`, `epilogue.txt`, `outro.txt`. A section titled `Intro` becomes `intro-2.txt`. Output names stay `<Name> 001.mp3`…`006.mp3`. |
| FR18 | Unticking the Introduction section (Order 1) does not untick chapter 1. Expanded previews and exclusions survive tab switches. |
| FR19 | `PrimaryLanguage("eng")` → `en`, `("POR-BR")` → `pt`, `("swe")` → `sv`, `("fra")` → `fra`. A book declaring `eng` with an English voice shows no mismatch warning and labels "English (eng)". |
| FR20 | Web logs for a strict import, an offer, and an accepted fallback contain only kinds, counts, totals, and the repair flag, with no title, label, text, or file name. |

## Test Cases
Tests use the existing xUnit `WebApp.Tests` project with in-memory `SyntheticEpubBuilder` streams and run in Docker through `make test` (the `web-test` stage). No real book text is used.

**Unit tests:**
- `WebApp.Tests/Epub/SyntheticEpubBuilder.cs`: add three fixtures.
  - `AsCalibreSplitEpub2()`: NCX `Chapter N: Title` labels targeting `split_000.html#<body id>`. Each `split_000` holds `<section epub:type="bodymatter chapter"><p>Chapter N</p></section>` and each `split_001` holds the heading, text, and a footnotes block. It also has Introduction (`frontmatter chapter introduction`), Epilogue, and Acknowledgments entries.
  - `AsWholeDocumentEpub3()`: `toc` nav links without fragments for Cover, Copyright, and `Chapter 1..3`, each chapter file starting `<h1>N.</h1>`, plus `landmarks` and `page-list` navs.
  - `AsPageListEpub2()`: NCX `1`, `2`, `3` → `text.html#page-N` empty divs, all text in one file, image-only leaf documents, language `eng`.
- `WebApp.Tests/Epub/EpubChapterParserTests.cs`:
  - FR1–FR10 cases above.
  - All existing ported tests unchanged and passing.
  - `ReadReadingOrderAsync` returns spine-order paragraphs, applies footnote skipping, respects `MaxBookCharacters`, and still rejects encrypted and non-ZIP input.
- `WebApp.Tests/Epub/EpubBookRulesTests.cs`:
  - `PrimaryLanguage`/`LanguageMismatch` with `eng`/`por`/`swe`/`fra`.
  - `SectionKeyword` per language.
  - `SectionFileName` slugs, diacritics, empty slug, reserved and duplicate names.
  - `SplitIntoParts` balance, the exact-multiple boundary, oversized paragraph, and empty input.
  - `TrackErrors` messages for chapter and parts structures.
- `WebApp.Tests/Epub/BookTrackBuilderTests.cs`:
  - Section and part text formats per language.
  - Ordering by `ItemOrder` with mixed kinds.
  - Exclusion keyed by `Order`.
  - Characters and bytes match the upload.
- `WebApp.Tests/Epub/EpubImportServiceTests.cs`:
  - A strict success returns `Book` and no offer.
  - The page-list fixture returns an offer with N parts and the reason.
  - Encrypted input throws with no offer.
  - Image-only input throws the no-readable-text message.
  - The temporary file is deleted in every case.
  - Logs contain no title or text.

**Integration tests:**
- Full stack, done manually because there is no browser or component harness: run `make docker-run-bg`, import each of the three real books, start one batch, and confirm `GET /batches/{id}` lists the FR17 names in order.
- ⚠️ TODO: tab and offer rendering (FR13–FR15) remain manual until a component test harness (bUnit/Playwright) is introduced.
- The worker's pytest suite must pass unchanged.

## Manual Verification
1. Run `make test`. Pytest, `WebApp.Tests`, and the runtime-stage compile all pass.
2. Run `make docker-run-bg` and open http://localhost:8082/batch-audio?tab=epub. Wait for "Voice engine ready", and make sure an English voice exists.
3. Choose `~/PereneArchive/Books/Press Reset Ruin and Recovery in the Video Game Industry - Jason Schreier.epub`. It imports with "9 chapters · 2 sections". Introduction and Epilogue are unticked.
   - Preview Chapter 1: it starts "Chapter One." and then the chapter's first paragraph, with no "Chapter 1" echo and no "Footnotes" block at the end.
   - Preview Chapter 9: it ends before the Epilogue text.
4. Tick Epilogue. Its row shows `epilogue.txt`, and its preview starts "Epilogue.".
5. Choose `~/PereneArchive/Books/Sci-Fi/The Room - Jonas Karlsson.epub`. All numbered chapters appear, and Cover, Copyright, and Contents are not listed. Preview one chapter and confirm the heading and body.
6. Choose `~/PereneArchive/Books/The knight in rusty armor.epub`. The alert shows the strict reason and "…split its text into 4 parts…", and The Room stays on screen.
   - Select **Split into parts**. Rows "Part 1…N" appear, the summary shows "N parts · no chapters found", and the language shows "English (eng)" with no mismatch warning for the English voice.
7. Select Include none. The message reads "Include at least one part.". Select Include all.
8. Choose a fresh file while an offer is showing. The offer disappears.
9. Start a batch from the parts book. The Batches tab opens. The track names are `part-001.txt`…, and Pause and Resume work.
10. Run `make docker-logs`. Web log lines show counts and kinds only. Run `docker compose exec web ls -la /tmp` and confirm no leftover temp files.
11. Check paper-light and phone width. The offer row wraps, the button is reachable by keyboard with a visible focus outline, and the alert is announced.

## Definition of Done
- Requirements, Plan, and Validation docs are in this spec folder, with open questions recorded.
- All existing tests pass (`make test`: pytest, `WebApp.Tests`, runtime compile), including every previously ported parser test unchanged.
- New behavior has xUnit coverage in `WebApp.Tests/Epub/` using synthetic fixtures only.
- UI: `EpubBatchPanel.razor` and `app.css` handle the offer, parts, and sections consistently, with responsive, error, and accessibility states verified manually in both themes.
- No AI/agent, worker, batch-limit, or repair-step behavior changed.
- Vendor decisions (circuit memory) cite Microsoft Learn in `Plan.md`. The ZIP, XML, and upload evidence carries over from the previous spec.
- No new NuGet package, container, or service. `README.md` documents the new structures, sections, parts fallback, and language codes. `AGENTS.md` is refreshed through the init-agent skill.
- The sibling `book-notes-ia` project is unmodified, and no real book or extracted text is committed.

## Rollback Plan
- **Hide only the fallback:** stop catching `UnsupportedStructure` in `EpubImportService` so strict failures throw again. The panel then never receives an offer, and the button cannot render.
- **Revert discovery:** restore the previous `EpubChapterParser.cs` from git. The new model fields keep defaults (`Kind = Chapter`, `Structure = Chapters`), so the panel continues to work with chapters only.
- **Full revert:** revert this spec's commit. No data migration is needed. Batches already created from parts or sections are ordinary worker batches and stay playable and deletable.
- **Disable all EPUB/batch creation without a deploy:** set `TTS_BATCH_ENABLED=false` on the worker.
