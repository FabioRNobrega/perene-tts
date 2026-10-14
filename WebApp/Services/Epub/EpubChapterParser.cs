using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using PereneTts.Models;

// Ported from book-notes-ia/services/EbookParseService.Api/Services/EpubChapterParser.cs.
// The input boundary changed: a seekable stream replaces the input-folder path, and the book text is capped.
// Perene discovery additions (Specs/20261014112604-epub-chapter-discovery-and-fallback): chapter-word labels,
// whole-document and body-anchored targets, epub:type chapter confirmation, named sections, footnote skipping,
// label-echo removal, continuation that stops at any TOC target, and reading-order text for the parts fallback.
namespace PereneTts.Services.Epub;

public sealed partial class EpubChapterParser(IOptions<EpubParserOptions> options) : IEpubChapterParser
{
    private const string ContainerPath = "META-INF/container.xml";
    private const string EpubNamespace = "http://www.idpf.org/2007/ops";
    private const string DublinCoreNamespace = "http://purl.org/dc/elements/1.1/";
    private static readonly HashSet<string> NarrativeBlocks =
        ["p", "blockquote", "li", "dt", "dd", "h3", "h4", "h5", "h6"];
    private static readonly HashSet<string> FootnoteTypes =
        ["footnote", "endnote", "rearnote", "footnotes", "endnotes", "rearnotes"];
    private static readonly HashSet<string> ExcludedElements =
        ["script", "style", "nav", "svg", "math", "audio", "video", "canvas"];
    private readonly EpubParserOptions _options = options.Value;

    public Task<ParsedEpubBook> ParseAsync(Stream epub, CancellationToken cancellationToken = default) =>
        Task.FromResult(Read(epub, requireNavigation: true, cancellationToken,
            (entries, metadata) => DiscoverBook(entries, metadata, cancellationToken)));

    public Task<EpubReadingText> ReadReadingOrderAsync(Stream epub, CancellationToken cancellationToken = default) =>
        Task.FromResult(Read(epub, requireNavigation: false, cancellationToken,
            (entries, metadata) => ReadSpineText(entries, metadata, cancellationToken)));

    private T Read<T>(
        Stream epub,
        bool requireNavigation,
        CancellationToken cancellationToken,
        Func<Dictionary<string, ZipArchiveEntry>, PackageMetadata, T> read)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!epub.CanSeek)
            {
                throw InvalidEpub("The EPUB could not be read safely.");
            }

            epub.Position = 0;
            using var archive = new ZipArchive(epub, ZipArchiveMode.Read, leaveOpen: true);
            ValidateArchive(archive);
            var entries = BuildEntryMap(archive);

            if (entries.ContainsKey("META-INF/encryption.xml"))
            {
                throw new EpubParseException(
                    EpubParseErrorKind.UnsupportedContent,
                    "Encrypted or DRM-protected EPUB content is not supported.");
            }

            var container = LoadXml(RequireEntry(entries, ContainerPath), ContainerPath);
            XNamespace containerNs = "urn:oasis:names:tc:opendocument:xmlns:container";
            var rootfile = container.Descendants(containerNs + "rootfile")
                .Select(element => (string?)element.Attribute("full-path"))
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            if (rootfile is null)
            {
                throw InvalidEpub("The EPUB container does not declare a package document.");
            }

            var packagePath = NormalizeArchivePath(string.Empty, rootfile);
            var package = LoadXml(RequireEntry(entries, packagePath), packagePath);
            return read(entries, ReadPackage(package, packagePath, requireNavigation));
        }
        catch (EpubParseException)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw InvalidEpub("The file is not a valid EPUB archive.", exception);
        }
        catch (XmlException exception)
        {
            throw InvalidEpub("A required EPUB XML document is malformed or exceeds configured limits.", exception);
        }
        catch (IOException exception)
        {
            throw InvalidEpub("The EPUB could not be read safely.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw InvalidEpub("The EPUB could not be read safely.", exception);
        }
        catch (OverflowException exception)
        {
            throw InvalidEpub("The EPUB exceeds configured archive limits.", exception);
        }
        catch (ArgumentException exception)
        {
            throw InvalidEpub("The EPUB contains an invalid path or XML value.", exception);
        }
    }

    private ParsedEpubBook DiscoverBook(
        Dictionary<string, ZipArchiveEntry> entries,
        PackageMetadata metadata,
        CancellationToken cancellationToken)
    {
        var navigation = LoadXml(RequireEntry(entries, metadata.NavigationPath), metadata.NavigationPath);
        IReadOnlyList<ParsedChapter> chapters;
        var language = metadata.Language;
        if (metadata.NavigationKind == NavigationKind.Epub3)
        {
            if (language.Length == 0)
            {
                throw InvalidEpub("The EPUB package is missing required language metadata.");
            }

            chapters = DiscoverEpub3Chapters(entries, metadata, navigation, cancellationToken);
        }
        else
        {
            var discovery = DiscoverNcxChapters(
                entries,
                metadata,
                navigation,
                requireInferredLanguage: language.Length == 0,
                cancellationToken);
            chapters = discovery.Chapters;
            if (language.Length == 0)
            {
                language = discovery.InferredLanguage
                    ?? throw InvalidEpub("The EPUB has no consistent explicit chapter language.");
            }
        }

        EnforceBookCharacterLimit(chapters.SelectMany(chapter => chapter.Paragraphs));
        return new ParsedEpubBook(metadata.Title, language, chapters);
    }

    // Fallback text: every spine XHTML document in reading order, with the same extraction rules and limits.
    private EpubReadingText ReadSpineText(
        Dictionary<string, ZipArchiveEntry> entries,
        PackageMetadata metadata,
        CancellationToken cancellationToken)
    {
        var paragraphs = new List<string>();
        foreach (var path in metadata.SpineXhtmlPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            paragraphs.AddRange(ExtractDocumentParagraphs(LoadXml(RequireEntry(entries, path), path)));
        }

        EnforceBookCharacterLimit(paragraphs);
        return new EpubReadingText(metadata.Title, metadata.Language, paragraphs);
    }

    private void EnforceBookCharacterLimit(IEnumerable<string> paragraphs)
    {
        long total = 0;
        foreach (var paragraph in paragraphs)
        {
            total += paragraph.Length;
            if (total > _options.MaxBookCharacters)
            {
                throw new EpubParseException(
                    EpubParseErrorKind.UnsupportedContent,
                    string.Create(CultureInfo.InvariantCulture, $"The book's text exceeds {_options.MaxBookCharacters:N0} characters."));
            }
        }
    }

    private void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count == 0
            || archive.Entries[0].FullName != "mimetype"
            || ReadSmallText(archive.Entries[0], 64) != "application/epub+zip")
        {
            throw InvalidEpub("The archive does not have a conforming EPUB media-type marker.");
        }

        if (archive.Entries.Count > _options.MaxArchiveEntryCount)
        {
            throw InvalidEpub("The EPUB exceeds the configured archive entry limit.");
        }

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > _options.MaxEntryBytes)
            {
                throw InvalidEpub("An EPUB resource exceeds the configured entry size limit.");
            }

            total = checked(total + entry.Length);
            if (total > _options.MaxTotalUncompressedBytes)
            {
                throw InvalidEpub("The EPUB exceeds the configured uncompressed size limit.");
            }
        }
    }

    private static Dictionary<string, ZipArchiveEntry> BuildEntryMap(ZipArchive archive)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries.Where(item => !string.IsNullOrEmpty(item.Name)))
        {
            var normalized = NormalizeArchivePath(string.Empty, entry.FullName);
            if (!entries.TryAdd(normalized, entry))
            {
                throw InvalidEpub("The EPUB contains duplicate archive resource paths.");
            }
        }

        return entries;
    }

    private PackageMetadata ReadPackage(XDocument package, string packagePath, bool requireNavigation)
    {
        var root = package.Root ?? throw InvalidEpub("The EPUB package document is empty.");
        var opf = root.Name.Namespace;
        XNamespace dc = DublinCoreNamespace;
        var title = NormalizeWhitespace(root.Descendants(dc + "title").FirstOrDefault()?.Value);
        var language = NormalizeWhitespace(root.Descendants(dc + "language").FirstOrDefault()?.Value);
        if (title.Length == 0)
        {
            throw InvalidEpub("The EPUB package is missing required title metadata.");
        }

        var manifest = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
        foreach (var item in root.Descendants(opf + "manifest").Elements(opf + "item"))
        {
            var id = (string?)item.Attribute("id");
            var href = (string?)item.Attribute("href");
            var mediaType = (string?)item.Attribute("media-type");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(mediaType))
            {
                throw InvalidEpub("The EPUB manifest contains an incomplete item.");
            }

            var resolved = NormalizeArchivePath(packagePath, href);
            if (!manifest.TryAdd(id, new ManifestItem(id, resolved, mediaType, Tokenize((string?)item.Attribute("properties")))))
            {
                throw InvalidEpub("The EPUB manifest contains duplicate identifiers.");
            }
        }

        var spineElements = root.Descendants(opf + "spine").ToList();
        if (spineElements.Count != 1)
        {
            throw InvalidEpub("The EPUB package must contain one spine.");
        }

        var spineElement = spineElements[0];
        var spine = spineElement.Elements(opf + "itemref")
            .Select(item => (string?)item.Attribute("idref"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToList();
        if (spine.Count == 0 || spine.Any(id => !manifest.ContainsKey(id)))
        {
            throw InvalidEpub("The EPUB package has an invalid or empty spine.");
        }

        var navigationItems = manifest.Values.Where(item => item.Properties.Contains("nav")).ToList();
        var xhtmlPaths = manifest.Values
            .Where(item => string.Equals(item.MediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Path)
            .ToHashSet(StringComparer.Ordinal);
        var spineXhtmlPaths = spine
            .Where(id => string.Equals(manifest[id].MediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
            .Select(id => manifest[id].Path)
            .ToList();

        // The parts fallback reads only the spine, so it does not depend on usable navigation.
        if (!requireNavigation)
        {
            return new PackageMetadata(title, language, string.Empty, NavigationKind.Ncx, xhtmlPaths, spineXhtmlPaths);
        }

        if (navigationItems.Count > 1)
        {
            throw UnsupportedStructure("The EPUB declares multiple EPUB 3 navigation documents.");
        }

        if (navigationItems.Count == 1)
        {
            if (!string.Equals(navigationItems[0].MediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
            {
                throw UnsupportedStructure("The EPUB 3 navigation item must be an XHTML document.");
            }

            return new PackageMetadata(title, language, navigationItems[0].Path, NavigationKind.Epub3, xhtmlPaths, spineXhtmlPaths);
        }

        var ncxId = NormalizeWhitespace((string?)spineElement.Attribute("toc"));
        if (ncxId.Length == 0
            || !manifest.TryGetValue(ncxId, out var ncxItem)
            || !string.Equals(ncxItem.MediaType, "application/x-dtbncx+xml", StringComparison.OrdinalIgnoreCase))
        {
            throw UnsupportedStructure(
                "The EPUB must declare an EPUB 3 navigation document or a spine-referenced EPUB 2 NCX document.");
        }

        return new PackageMetadata(title, language, ncxItem.Path, NavigationKind.Ncx, xhtmlPaths, spineXhtmlPaths);
    }

    private IReadOnlyList<ParsedChapter> DiscoverEpub3Chapters(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        PackageMetadata metadata,
        XDocument navigation,
        CancellationToken cancellationToken)
    {
        XNamespace epub = EpubNamespace;
        var toc = navigation.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "nav"
                && Tokenize((string?)element.Attribute(epub + "type")).Contains("toc"));
        if (toc is null)
        {
            throw UnsupportedStructure("The EPUB navigation document has no table of contents.");
        }

        var documentCache = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        var candidates = new List<ChapterCandidate>();
        var numericLabels = new HashSet<int>();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var targetPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var link in toc.Descendants().Where(element => element.Name.LocalName == "a"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var href = (string?)link.Attribute("href");
            if (string.IsNullOrWhiteSpace(href))
            {
                throw UnsupportedStructure("A table-of-contents link has no target.");
            }

            var (resourcePath, fragment) = ResolveNavigationTarget(metadata.NavigationPath, href);
            if (!metadata.XhtmlPaths.Contains(resourcePath))
            {
                throw UnsupportedStructure("A table-of-contents target is outside the declared XHTML manifest.");
            }

            targetPaths.Add(resourcePath);
            var document = GetContentDocument(resourcePath, entries, documentCache);
            XElement? target = null;
            if (fragment is not null)
            {
                target = FindTarget(document, fragment)
                    ?? throw UnsupportedStructure("A table-of-contents fragment target is missing.");
            }

            var label = NormalizeWhitespace(link.Value);
            var numericMatch = NumericLabel().Match(label);
            if (!numericMatch.Success)
            {
                numericMatch = ChapterWordLabel().Match(label);
            }

            var numeric = numericMatch.Success ? int.Parse(numericMatch.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
            var wholeDocument = target is null || IsDocumentRoot(target);
            var candidate = wholeDocument
                ? WholeDocumentCandidate(resourcePath, document, label, numeric, allowUnnumberedChapters: true)
                : FragmentCandidate(resourcePath, target!, label, numeric, allowUnnumberedChapters: true);
            if (candidate is null)
            {
                continue;
            }

            var key = wholeDocument ? resourcePath : resourcePath + "#" + fragment;
            if (!targets.Add(key))
            {
                throw UnsupportedStructure("The EPUB table of contents contains a duplicate chapter target.");
            }

            if (candidate.Kind == BookItemKind.Chapter && numeric.HasValue && !numericLabels.Add(numeric.Value))
            {
                throw UnsupportedStructure("The EPUB table of contents contains a duplicate chapter number.");
            }

            candidates.Add(candidate);
        }

        if (!candidates.Any(candidate => candidate.Kind == BookItemKind.Chapter))
        {
            throw UnsupportedStructure("No chapters matched the supported EPUB navigation structures.");
        }

        return BuildItems(candidates, targetPaths, metadata, entries, documentCache, useDeclaredNumbers: false);
    }

    private NcxDiscovery DiscoverNcxChapters(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        PackageMetadata metadata,
        XDocument navigation,
        bool requireInferredLanguage,
        CancellationToken cancellationToken)
    {
        var navMaps = navigation.Descendants()
            .Where(element => element.Name.LocalName == "navMap")
            .ToList();
        if (navMaps.Count != 1)
        {
            throw UnsupportedStructure("The EPUB 2 NCX document must contain one navigation map.");
        }

        var documentCache = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        var candidates = new List<ChapterCandidate>();
        var numericLabels = new HashSet<int>();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var targetPaths = new HashSet<string>(StringComparer.Ordinal);
        var inferredLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? inferredLanguage = null;

        foreach (var navPoint in navMaps[0].Descendants().Where(element => element.Name.LocalName == "navPoint"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var navLabel = navPoint.Elements().FirstOrDefault(element => element.Name.LocalName == "navLabel");
            var label = NormalizeWhitespace(navLabel?.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "text")?.Value);
            var content = navPoint.Elements().FirstOrDefault(element => element.Name.LocalName == "content");
            var href = (string?)content?.Attribute("src");
            if (!TryParseNcxChapterNumber(label, out var numeric))
            {
                // Unnumbered entries never fail the import: they only mark continuation stops or become named sections.
                if (TryUnnumberedNcxEntry(label, href, metadata, entries, documentCache, targetPaths) is { } section)
                {
                    if (!targets.Add(section.TargetKey))
                    {
                        throw UnsupportedStructure("The EPUB 2 NCX contains a duplicate chapter target.");
                    }

                    candidates.Add(section.Candidate);
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(href))
            {
                throw UnsupportedStructure("An EPUB 2 NCX chapter entry has no content target.");
            }

            var (resourcePath, fragment) = ResolveNcxTarget(metadata.NavigationPath, href);
            if (!metadata.XhtmlPaths.Contains(resourcePath))
            {
                throw UnsupportedStructure("An EPUB 2 NCX chapter target is outside the declared XHTML manifest.");
            }

            targetPaths.Add(resourcePath);
            var document = GetContentDocument(resourcePath, entries, documentCache);
            ChapterCandidate candidate;
            if (fragment is not null
                && (FindTarget(document, fragment) ?? throw UnsupportedStructure("An EPUB 2 NCX chapter fragment target is missing.")) is var target
                && !IsDocumentRoot(target))
            {
                var boundary = FindNumericBoundary(target, numeric) ?? FindSemanticChapter(target)
                    ?? throw UnsupportedStructure("An EPUB 2 NCX chapter target does not match its numeric heading.");
                candidate = new ChapterCandidate(resourcePath, target, boundary, numeric, IsWholeDocumentTarget: false, BookItemKind.Chapter, label);
            }
            else
            {
                fragment = null;
                candidate = WholeDocumentCandidate(resourcePath, document, label, numeric, allowUnnumberedChapters: false)
                    ?? throw UnsupportedStructure("An EPUB 2 NCX chapter does not have one matching numeric heading.");
            }

            var key = fragment is null ? resourcePath : resourcePath + "#" + fragment;
            if (!targets.Add(key))
            {
                throw UnsupportedStructure("The EPUB 2 NCX contains a duplicate chapter target.");
            }

            if (!numericLabels.Add(numeric))
            {
                throw UnsupportedStructure("The EPUB 2 NCX contains a duplicate chapter number.");
            }

            if (requireInferredLanguage)
            {
                var chapterLanguage = ResolveChapterLanguage(candidate.Target, candidate.Boundary);
                if (chapterLanguage.Length == 0)
                {
                    throw InvalidEpub("An EPUB 2 NCX chapter is missing required language metadata.");
                }

                inferredLanguage ??= chapterLanguage;
                inferredLanguages.Add(chapterLanguage);
                if (inferredLanguages.Count > 1)
                {
                    throw InvalidEpub("The EPUB 2 NCX chapters declare inconsistent languages.");
                }
            }

            candidates.Add(candidate);
        }

        if (!candidates.Any(candidate => candidate.Kind == BookItemKind.Chapter))
        {
            throw UnsupportedStructure("No chapters matched the supported EPUB 2 NCX structure.");
        }

        return new NcxDiscovery(
            BuildItems(candidates, targetPaths, metadata, entries, documentCache, useDeclaredNumbers: true),
            inferredLanguage);
    }

    // A malformed or missing non-chapter resource is ignored, as it was before sections existed.
    private (ChapterCandidate Candidate, string TargetKey)? TryUnnumberedNcxEntry(
        string label,
        string? href,
        PackageMetadata metadata,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IDictionary<string, XDocument> documentCache,
        ISet<string> targetPaths)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        try
        {
            var (resourcePath, fragment) = ResolveNcxTarget(metadata.NavigationPath, href);
            if (!metadata.XhtmlPaths.Contains(resourcePath))
            {
                return null;
            }

            targetPaths.Add(resourcePath);
            var document = GetContentDocument(resourcePath, entries, documentCache);
            var target = fragment is null ? null : FindTarget(document, fragment);
            if (fragment is not null && target is null)
            {
                return null;
            }

            var wholeDocument = target is null || IsDocumentRoot(target);
            var candidate = wholeDocument
                ? WholeDocumentCandidate(resourcePath, document, label, null, allowUnnumberedChapters: false)
                : FragmentCandidate(resourcePath, target!, label, null, allowUnnumberedChapters: false);
            return candidate is null ? null : (candidate, wholeDocument ? resourcePath : resourcePath + "#" + fragment);
        }
        catch (EpubParseException)
        {
            return null;
        }
    }

    // Fragment target: numbered chapters need a matching heading or epub:type chapter; unnumbered entries may be
    // named sections, and (EPUB 3 only) remaining semantic chapters stay chapters as in the reference.
    private static ChapterCandidate? FragmentCandidate(
        string resourcePath,
        XElement target,
        string label,
        int? numeric,
        bool allowUnnumberedChapters)
    {
        var semanticBoundary = FindSemanticChapter(target);
        if (numeric.HasValue)
        {
            var boundary = semanticBoundary ?? FindNumericBoundary(target, numeric.Value);
            return boundary is null
                ? null
                : new ChapterCandidate(resourcePath, target, boundary, numeric, IsWholeDocumentTarget: false, BookItemKind.Chapter, label);
        }

        if (IsNamedSection(label, target.AncestorsAndSelf().Where(IsStructural))
            && (IsStructural(target) || IsHeading(target)))
        {
            return new ChapterCandidate(resourcePath, target, target, null, IsWholeDocumentTarget: false, BookItemKind.Section, label);
        }

        return allowUnnumberedChapters && semanticBoundary is not null
            ? new ChapterCandidate(resourcePath, target, semanticBoundary, null, IsWholeDocumentTarget: false, BookItemKind.Chapter, label)
            : null;
    }

    // Whole-document target (no fragment, or a fragment on body/html): numbered chapters are confirmed by exactly
    // one matching numeric heading or by the document's first structural element being an epub:type chapter.
    private static ChapterCandidate? WholeDocumentCandidate(
        string resourcePath,
        XDocument document,
        string label,
        int? numeric,
        bool allowUnnumberedChapters)
    {
        var root = document.Root ?? throw InvalidEpub("A required EPUB XML document is empty.");
        var firstStructure = root.Descendants().FirstOrDefault(IsStructural);
        var semantic = firstStructure is not null && HasEpubType(firstStructure, "chapter") ? firstStructure : null;
        if (numeric.HasValue)
        {
            var headings = root.Descendants()
                .Where(element => IsHeading(element) && HasMatchingNumericHeading(element, numeric.Value))
                .ToList();
            if (headings.Count == 1)
            {
                return new ChapterCandidate(resourcePath, headings[0], headings[0], numeric, IsWholeDocumentTarget: true, BookItemKind.Chapter, label);
            }

            return semantic is null
                ? null
                : new ChapterCandidate(resourcePath, semantic, semantic, numeric, IsWholeDocumentTarget: true, BookItemKind.Chapter, label);
        }

        if (IsNamedSection(label, firstStructure is null ? [] : [firstStructure]))
        {
            return new ChapterCandidate(resourcePath, root, root, null, IsWholeDocumentTarget: true, BookItemKind.Section, label);
        }

        return allowUnnumberedChapters && semantic is not null
            ? new ChapterCandidate(resourcePath, semantic, semantic, null, IsWholeDocumentTarget: true, BookItemKind.Chapter, label)
            : null;
    }

    private static bool IsNamedSection(string label, IEnumerable<XElement> structures)
    {
        XNamespace epub = EpubNamespace;
        return EpubBookRules.SectionKeyword(label)
            || structures.Any(element => Tokenize((string?)element.Attribute(epub + "type")).Overlaps(EpubBookRules.SectionTypes));
    }

    private static XElement? FindSemanticChapter(XElement target) =>
        target.AncestorsAndSelf().FirstOrDefault(element => IsStructural(element) && HasEpubType(element, "chapter"));

    private static XElement? FindTarget(XDocument document, string fragment) =>
        document.Root?.DescendantsAndSelf()
            .FirstOrDefault(element => (string?)element.Attribute("id") == fragment
                || (string?)element.Attribute(XNamespace.Xml + "id") == fragment);

    private static bool IsDocumentRoot(XElement element) => element.Name.LocalName is "body" or "html";

    // Extracts every candidate, drops empty sections and label echoes, and assigns book order.
    private List<ParsedChapter> BuildItems(
        IReadOnlyList<ChapterCandidate> candidates,
        IReadOnlySet<string> targetPaths,
        PackageMetadata metadata,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IDictionary<string, XDocument> documentCache,
        bool useDeclaredNumbers)
    {
        var items = new List<ParsedChapter>(candidates.Count);
        int chapters = 0, sections = 0;
        foreach (var candidate in candidates)
        {
            var paragraphs = TrimLabelEcho(
                ExtractWithContinuation(candidate, candidates, targetPaths, metadata.SpineXhtmlPaths, entries, documentCache),
                candidate);
            if (candidate.Kind == BookItemKind.Section)
            {
                if (paragraphs.Count > 0)
                {
                    items.Add(new ParsedChapter(++sections, paragraphs, BookItemKind.Section, candidate.Label));
                }

                continue;
            }

            if (paragraphs.Count == 0)
            {
                throw UnsupportedStructure("A discovered chapter contains no narration-ready text.");
            }

            chapters++;
            items.Add(new ParsedChapter(useDeclaredNumbers ? candidate.DeclaredNumber!.Value : chapters, paragraphs));
        }

        return items.Select((item, index) => item with { Order = index + 1 }).ToList();
    }

    // Removes a first paragraph that only repeats the item's own label ("Chapter 1", "1.", or the section title).
    private static IReadOnlyList<string> TrimLabelEcho(IReadOnlyList<string> paragraphs, ChapterCandidate candidate)
    {
        if (paragraphs.Count == 0)
        {
            return paragraphs;
        }

        static string Echo(string value) => NormalizeWhitespace(value).TrimEnd('.', ':').Trim();
        var first = Echo(paragraphs[0]);
        var echoes = string.Equals(first, Echo(candidate.Label), StringComparison.OrdinalIgnoreCase);
        if (!echoes && candidate.Kind == BookItemKind.Chapter && candidate.DeclaredNumber is { } number)
        {
            var match = ChapterWordOnly().Match(first);
            echoes = first == number.ToString(CultureInfo.InvariantCulture)
                || (match.Success && match.Groups[1].Value == number.ToString(CultureInfo.InvariantCulture));
        }

        return echoes ? paragraphs.Skip(1).ToList() : paragraphs;
    }

    private static string ResolveChapterLanguage(XElement target, XElement boundary)
    {
        var scopedLanguage = target.AncestorsAndSelf()
            .Select(element => NormalizeWhitespace((string?)element.Attribute(XNamespace.Xml + "lang")))
            .FirstOrDefault(value => value.Length > 0);
        if (scopedLanguage is not null)
        {
            return scopedLanguage;
        }

        if (IsStructural(boundary))
        {
            var heading = boundary.Elements().FirstOrDefault(IsHeading);
            if (heading is not null)
            {
                return heading.AncestorsAndSelf()
                    .Select(element => NormalizeWhitespace((string?)element.Attribute(XNamespace.Xml + "lang")))
                    .FirstOrDefault(value => value.Length > 0) ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private XDocument GetContentDocument(
        string path,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IDictionary<string, XDocument> cache)
    {
        if (!cache.TryGetValue(path, out var document))
        {
            document = LoadXml(RequireEntry(entries, path), path);
            cache[path] = document;
        }

        return document;
    }

    private static XElement? FindNumericBoundary(XElement target, int number)
    {
        if (IsHeading(target) && HasMatchingNumericHeading(target, number))
        {
            return target;
        }

        if (!IsStructural(target))
        {
            return null;
        }

        var heading = target.Elements().FirstOrDefault(IsHeading);
        return heading is not null
            && HasMatchingNumericHeading(heading, number)
                ? target
                : null;
    }

    // A whole-document item continues through following spine documents until the next document any TOC entry
    // targets (chapter, section, or ignored entry), so the last chapter never absorbs back matter.
    private IReadOnlyList<string> ExtractWithContinuation(
        ChapterCandidate candidate,
        IReadOnlyList<ChapterCandidate> candidates,
        IReadOnlySet<string> targetPaths,
        IReadOnlyList<string> spineXhtmlPaths,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IDictionary<string, XDocument> documentCache)
    {
        var paragraphs = ExtractParagraphs(candidate, candidates).ToList();
        if (!candidate.IsWholeDocumentTarget)
        {
            return paragraphs;
        }

        var start = FindSpinePathIndex(spineXhtmlPaths, candidate.ResourcePath);
        if (start < 0)
        {
            return candidate.Kind == BookItemKind.Section
                ? paragraphs
                : throw UnsupportedStructure("A chapter target is outside the reading spine.");
        }

        for (var index = start + 1; index < spineXhtmlPaths.Count && !targetPaths.Contains(spineXhtmlPaths[index]); index++)
        {
            var document = GetContentDocument(spineXhtmlPaths[index], entries, documentCache);
            paragraphs.AddRange(ExtractDocumentParagraphs(document));
        }

        return paragraphs;
    }

    private static int FindSpinePathIndex(IReadOnlyList<string> spineXhtmlPaths, string path)
    {
        for (var index = 0; index < spineXhtmlPaths.Count; index++)
        {
            if (string.Equals(spineXhtmlPaths[index], path, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryParseNcxChapterNumber(string label, out int number)
    {
        number = 0;
        var match = ChapterWordLabel().Match(label);
        if (!match.Success)
        {
            match = NumericLabel().Match(label);
        }

        if (!match.Success)
        {
            match = NumberedNcxChapterTitle().Match(label);
        }

        return match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && number >= 1;
    }

    private static bool HasMatchingNumericHeading(XElement heading, int number)
    {
        var match = LeadingNumericHeading().Match(NormalizeWhitespace(heading.Value));
        return match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var headingNumber)
            && headingNumber == number;
    }

    private static IReadOnlyList<string> ExtractParagraphs(
        ChapterCandidate candidate,
        IReadOnlyList<ChapterCandidate> allCandidates)
    {
        IEnumerable<XElement> scope;
        if (IsStructural(candidate.Boundary) || candidate.Boundary.Parent is null)
        {
            scope = candidate.Boundary.Descendants();
        }
        else if (IsHeading(candidate.Boundary) && candidate.Boundary.Parent is not null)
        {
            var nextTargets = allCandidates
                .Where(other => ReferenceEquals(other.Target.Parent, candidate.Target.Parent))
                .Select(other => other.Target)
                .ToHashSet();
            scope = candidate.Boundary.ElementsAfterSelf().TakeWhile(element => !nextTargets.Contains(element));
        }
        else
        {
            throw UnsupportedStructure("A chapter target does not have an unambiguous structural boundary.");
        }

        return ExtractNarrativeBlocks(scope);
    }

    private static IReadOnlyList<string> ExtractDocumentParagraphs(XDocument document) =>
        ExtractNarrativeBlocks(document.Descendants());

    private static IReadOnlyList<string> ExtractNarrativeBlocks(IEnumerable<XElement> scope)
    {
        var blocks = scope
            .SelectMany(element => NarrativeBlocks.Contains(element.Name.LocalName)
                ? [element]
                : element.Descendants().Where(descendant => NarrativeBlocks.Contains(descendant.Name.LocalName)))
            .Where(element => !element.Ancestors().Any(ancestor => ExcludedElements.Contains(ancestor.Name.LocalName)))
            .Distinct()
            .Where(element => !element.Ancestors().Any(ancestor => NarrativeBlocks.Contains(ancestor.Name.LocalName)))
            .Where(element => !element.AncestorsAndSelf().Any(IsFootnote) && !IsFootnoteHeading(element));

        return blocks
            .Select(ReadNarrativeText)
            .Where(text => text.Length > 0)
            .ToList();
    }

    private static bool IsFootnote(XElement element)
    {
        XNamespace epub = EpubNamespace;
        return Tokenize((string?)element.Attribute(epub + "type")).Overlaps(FootnoteTypes);
    }

    // A "Footnotes" heading followed only by footnotes is part of the notes, not the narration.
    private static bool IsFootnoteHeading(XElement element)
    {
        if (!IsHeading(element))
        {
            return false;
        }

        var following = element.ElementsAfterSelf().ToList();
        return following.Count > 0 && following.All(IsFootnote);
    }

    private static string ReadNarrativeText(XElement block)
    {
        var text = string.Concat(block.DescendantNodesAndSelf()
            .OfType<XText>()
            .Where(node => !node.Ancestors().Any(IsExcludedInline))
            .Select(node => node.Value));
        return NormalizeWhitespace(text);
    }

    private static bool IsExcludedInline(XElement element)
    {
        if (ExcludedElements.Contains(element.Name.LocalName))
        {
            return true;
        }

        XNamespace epub = EpubNamespace;
        var types = Tokenize((string?)element.Attribute(epub + "type"));
        return types.Contains("noteref") || types.Contains("backlink") || types.Contains("pagebreak");
    }

    private XDocument LoadXml(ZipArchiveEntry entry, string logicalName)
    {
        if (entry.Length > _options.MaxEntryBytes)
        {
            throw InvalidEpub("A required EPUB document exceeds the configured entry size limit.");
        }

        try
        {
            using var stream = entry.Open();
            return ReadXml(stream);
        }
        catch (XmlException firstException)
        {
            try
            {
                using var stream = entry.Open();
                using var textReader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                var text = textReader.ReadToEnd();
                if (!text.Contains("&nbsp;", StringComparison.Ordinal))
                {
                    throw InvalidEpub(
                        "A required EPUB XML document is malformed or exceeds configured limits.",
                        firstException);
                }

                var normalized = text.Replace("&nbsp;", "&#160;", StringComparison.Ordinal);
                using var normalizedReader = new StringReader(normalized);
                return ReadXml(normalizedReader);
            }
            catch (EpubParseException)
            {
                throw;
            }
            catch (XmlException exception)
            {
                throw InvalidEpub("A required EPUB XML document is malformed or exceeds configured limits.", exception);
            }
        }
        catch (InvalidOperationException exception)
        {
            throw InvalidEpub("A required EPUB XML document could not be parsed.", exception);
        }
    }

    private XDocument ReadXml(Stream stream)
    {
        using var reader = XmlReader.Create(stream, CreateXmlReaderSettings());
        return XDocument.Load(reader, LoadOptions.None);
    }

    private XDocument ReadXml(TextReader textReader)
    {
        using var reader = XmlReader.Create(textReader, CreateXmlReaderSettings());
        return XDocument.Load(reader, LoadOptions.None);
    }

    private XmlReaderSettings CreateXmlReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        MaxCharactersInDocument = _options.MaxXmlCharacters,
        MaxCharactersFromEntities = 0,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = false
    };

    private static (string Path, string? Fragment) ResolveNavigationTarget(string referringPath, string href)
    {
        if (href.IndexOf('?', StringComparison.Ordinal) >= 0)
        {
            throw UnsupportedStructure("A table-of-contents link contains a query string.");
        }

        var hash = href.IndexOf('#');
        if (hash < 0)
        {
            return (NormalizeArchivePath(referringPath, href), null);
        }

        if (hash < 1 || hash == href.Length - 1)
        {
            throw UnsupportedStructure("Every supported chapter link must identify an XHTML document or fragment.");
        }

        var path = NormalizeArchivePath(referringPath, href[..hash]);
        var fragment = DecodeUriComponent(href[(hash + 1)..]);
        if (fragment.Length == 0 || fragment.Contains('/') || fragment.Contains('\\'))
        {
            throw UnsupportedStructure("A table-of-contents fragment is invalid.");
        }

        return (path, fragment);
    }

    private static (string Path, string? Fragment) ResolveNcxTarget(string referringPath, string href)
    {
        if (href.IndexOf('?', StringComparison.Ordinal) >= 0)
        {
            throw UnsupportedStructure("An EPUB 2 NCX chapter target contains a query string.");
        }

        var hash = href.IndexOf('#');
        if (hash < 0)
        {
            return (NormalizeArchivePath(referringPath, href), null);
        }

        if (hash < 1 || hash == href.Length - 1 || href.IndexOf('#', hash + 1) >= 0)
        {
            throw UnsupportedStructure("An EPUB 2 NCX chapter fragment is invalid.");
        }

        var path = NormalizeArchivePath(referringPath, href[..hash]);
        var fragment = DecodeUriComponent(href[(hash + 1)..]);
        if (fragment.Length == 0 || fragment.Contains('/') || fragment.Contains('\\'))
        {
            throw UnsupportedStructure("An EPUB 2 NCX chapter fragment is invalid.");
        }

        return (path, fragment);
    }

    private static string NormalizeArchivePath(string referringPath, string href)
    {
        if (string.IsNullOrWhiteSpace(href)
            || href.StartsWith('/')
            || href.Contains('\\')
            || href.Contains('?')
            || Uri.TryCreate(href, UriKind.Absolute, out _))
        {
            throw InvalidEpub("The EPUB contains an unsafe resource path.");
        }

        var decoded = DecodeUriComponent(href.Split('#')[0]);
        if (decoded.Contains('\\') || decoded.StartsWith('/'))
        {
            throw InvalidEpub("The EPUB contains an unsafe resource path.");
        }

        var baseParts = referringPath.Contains('/')
            ? referringPath[..(referringPath.LastIndexOf('/') + 1)].Split('/', StringSplitOptions.RemoveEmptyEntries).ToList()
            : [];
        foreach (var part in decoded.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (baseParts.Count == 0)
                {
                    throw InvalidEpub("The EPUB contains a resource path outside the archive root.");
                }

                baseParts.RemoveAt(baseParts.Count - 1);
                continue;
            }

            if (part.Contains(':') || part.IndexOf('\0') >= 0)
            {
                throw InvalidEpub("The EPUB contains an unsafe resource path.");
            }

            baseParts.Add(part);
        }

        if (baseParts.Count == 0)
        {
            throw InvalidEpub("The EPUB contains an empty resource path.");
        }

        return string.Join('/', baseParts);
    }

    private static ZipArchiveEntry RequireEntry(IReadOnlyDictionary<string, ZipArchiveEntry> entries, string path) =>
        entries.TryGetValue(path, out var entry)
            ? entry
            : throw InvalidEpub("A required EPUB resource is missing.");

    private static string ReadSmallText(ZipArchiveEntry entry, int maxCharacters)
    {
        if (entry.Length > maxCharacters)
        {
            return string.Empty;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        return text.Length <= maxCharacters ? text : string.Empty;
    }

    private static string DecodeUriComponent(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException exception)
        {
            throw InvalidEpub("The EPUB contains an invalid encoded resource path.", exception);
        }
    }

    private static HashSet<string> Tokenize(string? value) =>
        (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool HasEpubType(XElement element, string value)
    {
        XNamespace epub = EpubNamespace;
        return Tokenize((string?)element.Attribute(epub + "type")).Contains(value);
    }

    private static bool IsStructural(XElement element) =>
        element.Name.LocalName is "section" or "article";

    private static bool IsHeading(XElement element) =>
        element.Name.LocalName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6";

    private static string NormalizeWhitespace(string? value) =>
        Whitespace().Replace((value ?? string.Empty).Replace('\u00a0', ' '), " ").Trim();

    private static EpubParseException InvalidEpub(string message, Exception? inner = null) =>
        new(EpubParseErrorKind.InvalidEpub, message, inner);

    private static EpubParseException UnsupportedStructure(string message) =>
        new(EpubParseErrorKind.UnsupportedStructure, message);

    private sealed record ManifestItem(string Id, string Path, string MediaType, HashSet<string> Properties);
    private sealed record PackageMetadata(
        string Title,
        string Language,
        string NavigationPath,
        NavigationKind NavigationKind,
        HashSet<string> XhtmlPaths,
        IReadOnlyList<string> SpineXhtmlPaths);
    private sealed record ChapterCandidate(
        string ResourcePath,
        XElement Target,
        XElement Boundary,
        int? DeclaredNumber,
        bool IsWholeDocumentTarget,
        BookItemKind Kind,
        string Label);
    private sealed record NcxDiscovery(IReadOnlyList<ParsedChapter> Chapters, string? InferredLanguage);

    private enum NavigationKind
    {
        Epub3,
        Ncx
    }

    [GeneratedRegex(@"^0*([1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericLabel();

    [GeneratedRegex(@"^(?:Chapter|Cap[ií]tulo|Kapitel)\s+0*([1-9][0-9]*)(?:\s*[.:\-–—]\s*\S.*|\s+\S.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChapterWordLabel();

    [GeneratedRegex(@"^(?:Chapter|Cap[ií]tulo|Kapitel)\s+0*([1-9][0-9]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChapterWordOnly();

    [GeneratedRegex(@"^0*([1-9][0-9]*)(?:\s*[.\-–—:]\s*|\s+)\S.*$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedNcxChapterTitle();

    [GeneratedRegex(@"^0*([1-9][0-9]*)(?:$|[^0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingNumericHeading();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
