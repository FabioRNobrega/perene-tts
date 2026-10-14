using System.IO.Compression;
using System.Text;

// Adapted from book-notes-ia/services/EbookParseService.Tests/SyntheticEpubBuilder.cs: builds in memory, synthetic text only.
namespace WebApp.Tests.Epub;

internal sealed class SyntheticEpubBuilder
{
    private string _mimetype = "application/epub+zip";
    private bool _mimetypeFirst = true;
    private bool _mimetypeIncluded = true;
    private bool _encrypted;
    private string _package = DefaultPackage;
    private readonly Dictionary<string, string> _resources = new(StringComparer.Ordinal)
    {
        ["OPS/nav/toc.xhtml"] = DefaultNavigation,
        ["OPS/text/book.xhtml"] = DefaultContent
    };

    public SyntheticEpubBuilder WithWrongMimetype()
    {
        _mimetype = "application/zip";
        return this;
    }

    public SyntheticEpubBuilder WithMimetypeAfterContainer()
    {
        _mimetypeFirst = false;
        return this;
    }

    public SyntheticEpubBuilder WithoutMimetype()
    {
        _mimetypeIncluded = false;
        return this;
    }

    public SyntheticEpubBuilder WithEncryption()
    {
        _encrypted = true;
        return this;
    }

    public SyntheticEpubBuilder WithNavigation(string navigation)
    {
        _resources["OPS/nav/toc.xhtml"] = navigation;
        return this;
    }

    public SyntheticEpubBuilder WithContent(string content)
    {
        _resources["OPS/text/book.xhtml"] = content;
        return this;
    }

    public SyntheticEpubBuilder AsEpub2Ncx()
    {
        _package = Epub2Package;
        _resources.Clear();
        _resources["OPS/toc.ncx"] = DefaultNcx;
        _resources["OPS/text/chapter1.xhtml"] = Epub2ChapterOne;
        _resources["OPS/text/chapter2.xhtml"] = Epub2ChapterTwo;
        return this;
    }

    /// <summary>Calibre-style EPUB 2: "Chapter N: Title" labels on body-anchored split files, epub:type sections, footnotes.</summary>
    public SyntheticEpubBuilder AsCalibreSplitEpub2()
    {
        _package = CalibrePackage;
        _resources.Clear();
        _resources["OPS/toc.ncx"] = CalibreNcx;
        _resources["OPS/text/part0005.html"] = CalibreDocument("b5", "frontmatter chapter introduction", "<h1>Introduction</h1><p>Synthetic introduction prose.</p>");
        _resources["OPS/text/part0006_split_000.html"] = CalibreDocument("b6", "bodymatter chapter", "<div><p>Chapter 1</p></div>");
        _resources["OPS/text/part0006_split_001.html"] = CalibreDocument("b6", "bodymatter chapter",
            "<div><h1>The Journeyman</h1></div><p>Synthetic journeyman prose.</p><p>More journeyman prose<a epub:type=\"noteref\" href=\"#n1\">1</a>.</p>"
            + "<div><h4>Footnotes</h4><p epub:type=\"footnote\">Synthetic footnote text.</p></div>");
        _resources["OPS/text/part0007_split_000.html"] = CalibreDocument("b7", "bodymatter chapter", "<div><p>Chapter 2</p></div>");
        _resources["OPS/text/part0007_split_001.html"] = CalibreDocument("b7", "bodymatter chapter", "<div><h1>Project Icarus</h1></div><p>Synthetic icarus prose.</p>");
        _resources["OPS/text/part0015.html"] = CalibreDocument("b15", "backmatter chapter epilogue", "<h1>Epilogue</h1><p>Synthetic epilogue prose.</p>");
        _resources["OPS/text/part0016.html"] = CalibreDocument("b16", "backmatter acknowledgments", "<h1>Acknowledgments</h1><p>Synthetic thanks.</p>");
        return this;
    }

    /// <summary>EPUB 3 whose toc links whole files labelled "Chapter N", with landmarks and page-list navs.</summary>
    public SyntheticEpubBuilder AsWholeDocumentEpub3()
    {
        _package = WholeDocumentPackage;
        _resources.Clear();
        _resources["OPS/nav.xhtml"] = WholeDocumentNavigation;
        _resources["OPS/cover.xhtml"] = "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>Synthetic cover caption.</p></body></html>";
        _resources["OPS/copyright.xhtml"] = "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>Synthetic copyright notice.</p></body></html>";
        for (var number = 1; number <= 3; number++)
        {
            _resources[$"OPS/c00{number}.xhtml"] =
                $"<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><body><a id=\"top{number}\"/><div><span epub:type=\"pagebreak\" id=\"page{number}\" title=\"{number}\"/><h1>{number}.</h1><p>Synthetic room prose {number}.</p></div></body></html>";
        }
        return this;
    }

    /// <summary>Scanned-style EPUB 2: NCX page numbers pointing at page-break divs, one text file, an image-only page, language "eng".</summary>
    public SyntheticEpubBuilder AsPageListEpub2(bool withText = true)
    {
        _package = PageListPackage;
        _resources.Clear();
        _resources["OPS/toc.ncx"] = PageListNcx;
        _resources["OPS/leaf0001.html"] = "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><div><img src=\"cover.jpg\" alt=\"\" /></div></body></html>";
        _resources["OPS/part0001.html"] = withText
            ? "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><div class=\"newpage\" id=\"page-2\"/><p> SYNTHETIC KNIGHT</p><p>Synthetic knight prose one.</p><div class=\"newpage\" id=\"page-3\"/><p>Synthetic knight prose two.</p></body></html>"
            : "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><div class=\"newpage\" id=\"page-2\"/><div class=\"newpage\" id=\"page-3\"/></body></html>";
        return this;
    }

    private static string CalibreDocument(string bodyId, string types, string inner) =>
        $"<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><body id=\"{bodyId}\"><div><section epub:type=\"{types}\">{inner}</section></div></body></html>";

    public SyntheticEpubBuilder WithPackage(string package)
    {
        _package = package;
        return this;
    }

    public SyntheticEpubBuilder WithResource(string path, string content)
    {
        _resources[path] = content;
        return this;
    }

    public byte[] BuildBytes() => Build().ToArray();

    public MemoryStream Build()
    {
        var file = new MemoryStream();
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive);
        }

        file.Position = 0;
        return file;
    }

    private void Write(ZipArchive archive)
    {
        if (_mimetypeIncluded && _mimetypeFirst)
        {
            Add(archive, "mimetype", _mimetype, CompressionLevel.NoCompression);
        }

        Add(archive, "META-INF/container.xml", Container);
        if (_mimetypeIncluded && !_mimetypeFirst)
        {
            Add(archive, "mimetype", _mimetype, CompressionLevel.NoCompression);
        }

        if (_encrypted)
        {
            Add(archive, "META-INF/encryption.xml", "<encryption xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" />");
        }

        Add(archive, "OPS/package.opf", _package);
        foreach (var (resourcePath, content) in _resources)
        {
            Add(archive, resourcePath, content);
        }
    }

    private static void Add(
        ZipArchive archive,
        string name,
        string content,
        CompressionLevel compression = CompressionLevel.Optimal)
    {
        var entry = archive.CreateEntry(name, compression);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private const string Container = """
        <?xml version="1.0"?>
        <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
          <rootfiles><rootfile full-path="OPS/package.opf" media-type="application/oebps-package+xml" /></rootfiles>
        </container>
        """;

    internal const string DefaultPackage = """
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:identifier id="id">synthetic-book</dc:identifier>
            <dc:title>Á Sample Book</dc:title>
            <dc:language>en-US</dc:language>
          </metadata>
          <manifest>
            <item id="nav" href="nav/toc.xhtml" media-type="application/xhtml+xml" properties="nav" />
            <item id="book" href="text/book.xhtml" media-type="application/xhtml+xml" />
          </manifest>
          <spine><itemref idref="book" /></spine>
        </package>
        """;

    internal const string Epub2Package = """
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:identifier id="id">synthetic-epub2-book</dc:identifier>
            <dc:title>NCX Sample Book</dc:title>
            <dc:language>en</dc:language>
          </metadata>
          <manifest>
            <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml" />
            <item id="chapter1" href="text/chapter1.xhtml" media-type="application/xhtml+xml" />
            <item id="chapter2" href="text/chapter2.xhtml" media-type="application/xhtml+xml" />
          </manifest>
          <spine toc="ncx"><itemref idref="chapter1" /><itemref idref="chapter2" /></spine>
        </package>
        """;

    internal const string DefaultNcx = """
        <?xml version="1.0" encoding="utf-8"?>
        <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
          <navMap>
            <navPoint id="preface" playOrder="1">
              <navLabel><text>Preface</text></navLabel><content src="text/not-in-manifest.xhtml" />
            </navPoint>
            <navPoint id="chapter1" playOrder="2">
              <navLabel><text>Chapter 1</text></navLabel><content src="text/chapter1.xhtml" />
            </navPoint>
            <navPoint id="chapter2" playOrder="3">
              <navLabel><text>Chapter 2</text></navLabel><content src="text/chapter2.xhtml" />
            </navPoint>
          </navMap>
        </ncx>
        """;

    internal const string Epub2ChapterOne = """
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.1//EN" "http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd">
        <html xmlns="http://www.w3.org/1999/xhtml">
          <head><title>Chapter 1</title></head>
          <body><h2>1</h2><p>First NCX chapter paragraph.</p><p>More chapter one.</p></body>
        </html>
        """;

    internal const string Epub2ChapterTwo = """
        <?xml version="1.0" encoding="utf-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml">
          <head><title>Chapter 2</title></head>
          <body><h2>2</h2><p>Only chapter two NCX prose.</p></body>
        </html>
        """;

    internal const string CalibrePackage = """
        <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">calibre</dc:identifier><dc:title>Calibre Sample</dc:title><dc:language>en</dc:language></metadata>
          <manifest>
            <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml" />
            <item id="intro" href="text/part0005.html" media-type="application/xhtml+xml" />
            <item id="c1a" href="text/part0006_split_000.html" media-type="application/xhtml+xml" />
            <item id="c1b" href="text/part0006_split_001.html" media-type="application/xhtml+xml" />
            <item id="c2a" href="text/part0007_split_000.html" media-type="application/xhtml+xml" />
            <item id="c2b" href="text/part0007_split_001.html" media-type="application/xhtml+xml" />
            <item id="epi" href="text/part0015.html" media-type="application/xhtml+xml" />
            <item id="ack" href="text/part0016.html" media-type="application/xhtml+xml" />
          </manifest>
          <spine toc="ncx"><itemref idref="intro" /><itemref idref="c1a" /><itemref idref="c1b" /><itemref idref="c2a" /><itemref idref="c2b" /><itemref idref="epi" /><itemref idref="ack" /></spine>
        </package>
        """;

    internal const string CalibreNcx = """
        <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><navMap>
          <navPoint id="n5"><navLabel><text>Introduction</text></navLabel><content src="text/part0005.html#b5" /></navPoint>
          <navPoint id="n6"><navLabel><text>Chapter 1: The Journeyman</text></navLabel><content src="text/part0006_split_000.html#b6" /></navPoint>
          <navPoint id="n7"><navLabel><text>Chapter 2: Project Icarus</text></navLabel><content src="text/part0007_split_000.html#b7" /></navPoint>
          <navPoint id="n15"><navLabel><text>Epilogue</text></navLabel><content src="text/part0015.html#b15" /></navPoint>
          <navPoint id="n16"><navLabel><text>Acknowledgments</text></navLabel><content src="text/part0016.html#b16" /></navPoint>
        </navMap></ncx>
        """;

    internal const string WholeDocumentPackage = """
        <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">room</dc:identifier><dc:title>Room Sample</dc:title><dc:language>en-US</dc:language></metadata>
          <manifest>
            <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav" />
            <item id="cover" href="cover.xhtml" media-type="application/xhtml+xml" />
            <item id="copyright" href="copyright.xhtml" media-type="application/xhtml+xml" />
            <item id="c1" href="c001.xhtml" media-type="application/xhtml+xml" />
            <item id="c2" href="c002.xhtml" media-type="application/xhtml+xml" />
            <item id="c3" href="c003.xhtml" media-type="application/xhtml+xml" />
          </manifest>
          <spine><itemref idref="cover" /><itemref idref="copyright" /><itemref idref="c1" /><itemref idref="c2" /><itemref idref="c3" /></spine>
        </package>
        """;

    internal const string WholeDocumentNavigation = """
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><body>
          <nav epub:type="toc"><ol>
            <li><a href="cover.xhtml">Cover</a></li>
            <li><a href="copyright.xhtml">Copyright</a></li>
            <li><a href="c001.xhtml">Chapter 1</a></li>
            <li><a href="c002.xhtml">Chapter 2</a></li>
            <li><a href="c003.xhtml">Chapter 3</a></li>
          </ol></nav>
          <nav epub:type="landmarks"><ol><li><a epub:type="bodymatter" href="c001.xhtml">Start</a></li></ol></nav>
          <nav epub:type="page-list"><ol><li><a href="c001.xhtml#page1">1</a></li><li><a href="c002.xhtml#page2">2</a></li></ol></nav>
        </body></html>
        """;

    internal const string PageListPackage = """
        <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">knight</dc:identifier><dc:title>Knight Sample</dc:title><dc:language>eng</dc:language></metadata>
          <manifest>
            <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml" />
            <item id="leaf" href="leaf0001.html" media-type="application/xhtml+xml" />
            <item id="text" href="part0001.html" media-type="application/xhtml+xml" />
          </manifest>
          <spine toc="ncx"><itemref idref="leaf" /><itemref idref="text" /></spine>
        </package>
        """;

    internal const string PageListNcx = """
        <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><navMap>
          <navPoint id="cover"><navLabel><text>Front Cover</text></navLabel><content src="leaf0001.html" /></navPoint>
          <navPoint id="p1"><navLabel><text>1</text></navLabel><content src="part0001.html#page-2" /></navPoint>
          <navPoint id="p2"><navLabel><text>2</text></navLabel><content src="part0001.html#page-3" /></navPoint>
        </navMap></ncx>
        """;

    internal const string DefaultNavigation = """
        <?xml version="1.0" encoding="utf-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
          <body><nav epub:type="toc"><ol>
            <li><a href="../text/book.xhtml#title">Title</a></li>
            <li><a href="../text/book.xhtml#part-one">PART ONE</a></li>
            <li><a href="../text/book.xhtml#one">1</a></li>
            <li><a href="../text/book.xhtml#two">2</a></li>
            <li><a href="../text/book.xhtml#three">3</a></li>
            <li><a href="../text/book.xhtml#coda">CODA</a></li>
          </ol></nav></body>
        </html>
        """;

    internal const string DefaultContent = """
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
          <head><title>Fixture</title><style>.hidden { display:none; }</style></head>
          <body>
            <section id="title"><h1>A Sample Book</h1></section>
            <section id="part-one"><h1>PART ONE</h1></section>
            <section id="one"><h2>1</h2><p>First <em>synthetic</em> paragraph — safe.</p><p>Second paragraph<a epub:type="noteref" href="#note">1</a>.</p><script>private script text</script></section>
            <section id="two"><h2>2</h2><p>Only chapter two text.</p><nav>navigation noise</nav></section>
            <section id="three"><h2>3</h2><p>Only chapter three text.</p><img src="cover.png" alt="image words" /></section>
            <section id="coda"><h1>CODA</h1><p>Not a numbered chapter.</p></section>
          </body>
        </html>
        """;
}
