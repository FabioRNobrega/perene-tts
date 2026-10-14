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
