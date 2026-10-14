# Requirements: EPUB Chapter Discovery Relaxations and No-Chapter Fallback

## Table of Contents
- [Problem Statement](#problem-statement)
- [Evidence From Real Books](#evidence-from-real-books)
- [Discovery Decisions](#discovery-decisions)
- [User Stories](#user-stories)
- [Functional Requirements](#functional-requirements)
- [Non-Functional Requirements](#non-functional-requirements)
- [Out of Scope](#out-of-scope)
- [Open Questions](#open-questions)

## Problem Statement
The **From EPUB** tab added in `Specs/20261014103908-epub-book-batch-import` uses `WebApp/Services/Epub/EpubChapterParser.cs`, a near-verbatim port of the strict book-notes-ia parser. It accepts only a few exact table-of-contents patterns, so three ordinary, DRM-free books are rejected:
- A Calibre-converted EPUB 2 with labels such as `Chapter 1: The Journeyman`.
- An EPUB 3 whose navigation links whole chapter files instead of `#fragment`s.
- A scanned book whose table of contents lists page numbers instead of chapters.

The same scanned book declares its language as `eng`, which the language-mismatch warning treats as different from an English voice's `en`.

This spec does three things:
- Relaxes chapter discovery for the first two patterns while still requiring explicit chapter evidence.
- Adds opt-in support for named non-chapter sections (Introduction, Epilogue…).
- Offers a user-chosen fixed-size "parts" fallback for books with no usable chapter structure, and normalizes three-letter language codes.

The worker, `POST /batches`, batch limits, and the repair step are unchanged.

## Evidence From Real Books
These books were inspected read-only on 2026-10-14. They are private, are not committed, and are not copied into tests; synthetic fixtures reproduce their structure.

| Book | Structure found | Current failure |
| --- | --- | --- |
| *Press Reset* (EPUB 2, `en`) | NCX labels `Chapter N: Title`. Each target is `text/partNNNN_split_000.html#<body id>`. Calibre split every chapter: `split_000` holds only `<p>Chapter 1</p>`, and `split_001` holds `<h1>Title</h1>` and the text. Every section is `<section epub:type="bodymatter chapter">`. *Introduction* is `epub:type="frontmatter chapter introduction"`, and *Epilogue* is `backmatter chapter epilogue`. Each chapter ends with `<h4>Footnotes</h4>` and `<p epub:type="footnote">` blocks. | "No chapters matched the supported EPUB 2 NCX structure." Labels match none of `Chapter N`, `N`, or `N. Title`. |
| *The Room* (EPUB 3, `en-US`) | The `toc` nav links whole files (`…_c001_r1.xhtml`, no `#`), labelled `Chapter 1` … `Chapter N`, plus Cover/Title/Copyright entries. Each chapter file starts `<h1>1.</h1>`. The nav also has `landmarks` and `page-list` navs. | "Every supported chapter link must identify an XHTML fragment." This is raised by the very first link (Cover). |
| *The Knight in Rusty Armor* (EPUB 2, `eng`) | NCX entries `1`, `2`, … point to `part0001.html#page-N` page-break `<div>`s. All ~90,900 characters are in one file. Chapter titles are plain capitalized `<p>` lines. Other spine files are image-only pages. | "An EPUB 2 NCX chapter target does not match its numeric heading." No reliable chapter structure exists. |

## Discovery Decisions
| Topic | Decision |
| --- | --- |
| Fallback trigger | Strict discovery runs first. When it fails on chapter structure, the error is shown together with an explicit **Split into parts** action. Nothing is applied silently. |
| Fallback method | Fixed size only. The book's reading-order text is cut into equal-sized parts at paragraph boundaries, regardless of file boundaries and without guessing titles. |
| Part size | Target about 30,000 characters (about 35 minutes of audio), configurable. |
| Named sections | Unnumbered narrative sections (Introduction, Prologue, Epilogue…) become importable tracks that are **unticked by default**. Front and back matter such as copyright, acknowledgments, and about-the-author stays excluded. |
| Parts heading | Parts have no spoken heading, because a fixed-size cut can fall mid-scene and an announced "Part Two" would interrupt the narration. The track name still identifies the part. Recorded under Open Questions. |
| Language codes | `eng`, `por`, and `swe` normalize to `en`, `pt`, and `sv` wherever the book language is compared or displayed. |

## User Stories
- Given *Press Reset*, when I choose it on From EPUB, then I see its nine numbered chapters with their text, plus unticked *Introduction* and *Epilogue* rows. Each chapter's text starts with the chapter body, not a repeated "Chapter 1" line, and contains no footnotes block.
- Given *The Room*, when I choose it, then I see every numbered chapter, and the Cover, Title, Copyright, and Contents entries are ignored instead of failing the import.
- Given *The Knight in Rusty Armor*, when I choose it, then I see why chapters could not be found and a **Split into parts** button with the part count. Selecting it loads four balanced parts (about 23,000 characters each, because ~91,000 characters at a 30,000 target rounds up to four) that I can preview, untick, and start as a batch.
- Given a book that declares `eng` and an English voice, when I import it, then no language-mismatch warning appears and the summary reads "English (eng)".
- Given a book that is encrypted, corrupt, or over the limits, when I choose it, then I see the error as today, with no Split into parts offer.

## Functional Requirements
1. FR1 — NCX chapter labels also accept a chapter word followed by a number and an optional title: `Chapter`, `Capítulo`, `Capitulo`, or `Kapitel` (case-insensitive), then `N`, then optionally a separator (`:`, `.`, `-`, `–`, `—`, or whitespace) and a title. Examples: `Chapter 1: The Journeyman`, `Capítulo 3 – O Fim`, `Kapitel 2`. The existing `N` and `N. Title` labels keep working.
2. FR2 — EPUB 3 `toc` labels accept the same chapter-word labels as FR1, in addition to bare numbers and `epub:type="chapter"`.
3. FR3 — A numbered table-of-contents entry is confirmed as a chapter when either of these holds:
   - The existing matching numeric heading rule holds.
   - The target, an ancestor of the target, or (for a whole-document target) the first structural `section`/`article` in the document carries `epub:type` containing `chapter`.

   Entries with neither are still not chapters.
4. FR4 — EPUB 3 `toc` links without a `#fragment` are accepted as whole-document targets. They are no longer rejected. Query strings, targets outside the declared XHTML manifest, and duplicate targets are still rejected.
5. FR5 — A fragment that resolves to the document's `body` or `html` element is treated as a whole-document target, so the spine continuation rule applies to it. This covers Calibre's `split_000#<body id>` targets.
6. FR6 — For both EPUB 2 and EPUB 3, a whole-document chapter or section continues through the following spine XHTML documents. It stops at the next spine document targeted by **any** table-of-contents entry, including ignored entries such as Acknowledgments, or at the end of the spine. This ensures the last chapter never absorbs back matter.
7. FR7 — A named section is an unnumbered table-of-contents entry whose target structure carries an `epub:type` of `introduction`, `preface`, `foreword`, `prologue`, `epilogue`, `afterword`, or `conclusion`, or whose label (trimmed, trailing punctuation ignored, case-insensitive) is one of these:
   - en: Introduction, Preface, Foreword, Prologue, Epilogue, Afterword, Conclusion.
   - pt: Introdução, Prefácio, Prólogo, Epílogo, Posfácio, Conclusão.
   - sv: Inledning, Förord, Prolog, Epilog, Efterord.

   Named sections take precedence over a generic `chapter` type for unnumbered entries. Unnumbered semantic chapters with other labels keep the existing EPUB 3 behavior and remain chapters.
8. FR8 — Named sections appear in book order among the chapters, are listed with their table-of-contents label, and start **unticked**. A named section with no narration-ready text is omitted rather than failing the import. A chapter with no text still fails, as today. Sections never count as chapters: a table of contents that yields sections but no chapters fails strict discovery with `UnsupportedStructure`, which leads to the FR11 offer.
9. FR9 — Narration extraction skips footnote content: elements whose `epub:type` contains `footnote`, `endnote`, or `rearnote` (or `footnotes`/`endnotes`/`rearnotes` on a container). A heading whose only following sibling blocks in its parent are such footnotes is dropped too, which removes the `Footnotes` heading.
10. FR10 — A chapter's first narrative paragraph is dropped when it only repeats the chapter's own label: its TOC label, `N`, `N.`, or `<chapter word> N` for that chapter's number. A section's first paragraph is dropped when it equals the section title. This prevents "Chapter One. Chapter 1. …".
11. FR11 — When strict discovery fails with `EpubParseErrorKind.UnsupportedStructure`, the import reads all spine XHTML documents in reading order from the same prepared archive and in the same background task. It uses the same narrative-block, excluded-element, footnote, and XML rules. If any narration-ready text exists, it returns a fallback offer with the strict error message and a parts book.
    - Failures of any other kind (`InvalidEpub`, `UnsupportedContent`, `InvalidRequest`) never offer a fallback.
    - A book with no readable text (for example image-only scans) shows the strict error plus "This book has no readable text."
12. FR12 — The parts book has N = ceil(total characters / `FallbackPartCharacters`) parts (default 30,000). Paragraphs are assigned in order, and a part closes once it reaches total/N characters, so parts are balanced. A paragraph is never split. Parts are numbered 1…N, named `part-NNN.txt`, and narrated without a heading (FR16).
13. FR13 — On a fallback offer, the panel shows the strict reason in the existing `role="alert"` list, plus: "This book has no chapter structure we can rely on. You can split its text into N parts of about 30,000 characters instead." A secondary **Split into parts** button appears beside it. Selecting the button replaces the current import with the parts book. Choosing another file, Clear, or leaving the page discards the offer, and the previous import stays until the button is selected (FR4 of the previous spec).
14. FR14 — For a parts book:
    - The summary reads "N parts · no chapters found".
    - Rows read "Part N", are all ticked by default, and keep the include, preview, Include all, and Include none behavior.
    - The heading hint is not shown in the preview.
    - The start-requirement message reads "Include at least one part."
15. FR15 — For a chapter book with sections, the summary reads "N chapters · M sections". Rows read "Chapter N" or the section title. The start requirement message stays "Include at least one chapter.", and it is met when at least one chapter or section is included.
16. FR16 — Narrated text per kind:
    - Chapter: unchanged, `"<Label> <Number>.\n\n<paragraphs>\n"`.
    - Section: `"<Title>.\n\n<paragraphs>\n"`. The title is the TOC label with whitespace normalized and any existing trailing `.`, `!`, `?`, or `…` kept rather than doubled.
    - Part: `"<paragraphs>\n"` with no heading.

    All kinds use the voice-language normalizer.
17. FR17 — Upload names, in narration order:
    - `intro.txt`.
    - Chapters as `chapter-NNN.txt`.
    - Sections as `<slug>.txt`, where the slug is lowercase ASCII with diacritics removed and non-alphanumerics collapsed to `-` (e.g. `introduction.txt`, `epilogo.txt`). An empty slug becomes `section-NN.txt`, and duplicates get `-2`, `-3`. The names `intro.txt`, `outro.txt`, and `chapter-*.txt` are never produced for sections.
    - Parts as `part-NNN.txt`.
    - `outro.txt`.

    Output names stay positional (`BatchRules.OutputName`).
18. FR18 — Inclusion, previews, and track positions are keyed by each item's 1-based position in book order (not its chapter number), so sections and parts are selectable and previewable like chapters. The draft (`EpubImportDraft`) keeps this state across tabs and navigation, as today.
19. FR19 — `EpubBookRules.PrimaryLanguage` maps the three-letter codes `eng`, `por`, and `swe` (case-insensitive, with any region suffix) to `en`, `pt`, and `sv`. The language-mismatch warning and the summary's language label use it. Other codes are unchanged.
20. FR20 — Logs record only these facts: whether the strict parse or the fallback produced the book, the counts of chapters, sections, and parts, character totals, the error kind, and whether a repair ran. Logs never record titles, labels, text, or file names.

## Non-Functional Requirements
- **Strictness preserved:** every relaxation still requires explicit evidence, either a chapter-numbered TOC label confirmed by a heading or `epub:type`, or a semantic section type or keyword. A book never gets guessed chapters. The parts fallback is visible, user-initiated, and never named as chapters.
- **Memory (Interactive Server):** following Learn's resource-exhaustion guidance, the fallback is computed only after a strict failure. Its text is bounded by the existing `MaxBookCharacters`, and only one book (the import or the offer) is computed per pick. The offer is held in component state and released on a new pick, Clear, or dispose. The EPUB is never re-uploaded or stored.
- **Security:** all existing archive, path, and XML protections apply unchanged to the fallback reader, which reuses the parser's private helpers.
- **Design (SOLID):**
  - The parser keeps sole ownership of EPUB reading and gains a second read entry point for reading-order text.
  - Pure part-splitting, section keywords, slugs, and language mapping go in the static `EpubBookRules`.
  - `EpubImportService` decides between import and offer.
  - The panel holds only workflow state.
- **Compatibility:** books that import today keep the same chapters, numbers, text, and track names, except that duplicated label paragraphs (FR10) and footnote blocks (FR9) are now removed. The batch upload contract and the worker are unchanged.
- **Privacy and copyright:** real books stay outside the repo. Tests use synthetic EPUBs that mimic the three structures without real text.
- **No new dependencies:** BCL and existing ASP.NET Core APIs only.

## Out of Scope
- Detecting chapter titles from capitalized or short paragraphs, or any other title heuristic.
- Splitting parts at file boundaries, sentence-level splits, or splitting a single paragraph over the per-track limit (it shows as invalid, as today).
- Removing front matter (copyright page, printed contents) from fallback parts. It lands in Part 1 (see Open Questions).
- Using `page-list` or `landmarks` navs for chapter discovery.
- Changing EPUB 3 chapter numbering, which stays positional among chapters, or EPUB 2 numbering, which stays the declared number.
- Changes to the repair step, worker, batch limits, or MP3 settings.
- Spoken headings for parts.

## Open Questions
- ⚠️ TODO: In the fixed-size fallback, front-matter text (copyright notices, a printed contents page) ends up at the start of Part 1. A later change could skip spine documents typed `frontmatter copyright-page`/`toc`/`titlepage`. The Knight carries no such types, so this would not help that book.
- ⚠️ TODO: Parts have no spoken heading (decision above). Revisit after listening if listeners want "Part N." announcements.
- ⚠️ TODO: The pt/sv section keyword lists are a reasonable first set and may need additions after real Portuguese and Swedish books are tried.
