using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

// Ported from book-notes-ia/services/EbookParseService.Api/repair-ebook.sh as an automatic, stream-to-stream step.
// Only the mimetype marker and entry order are repaired; chapter structure stays with the strict parser.
namespace PereneTts.Services.Epub;

public sealed partial class EpubArchiveRepairer(IOptions<EpubParserOptions> options) : IEpubArchiveRepairer
{
    private const string Marker = "mimetype";
    private const string MarkerValue = "application/epub+zip";
    private const int SymbolicLinkMode = 0xA000;
    private readonly EpubParserOptions _options = options.Value;

    public Task<PreparedEpub> PrepareAsync(Stream source, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!source.CanSeek)
        {
            throw InvalidEpub("The source is not a valid ZIP archive.");
        }

        source.Position = 0;
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException exception)
        {
            throw InvalidEpub("The source is not a valid ZIP archive.", exception);
        }

        using (archive)
        {
            try
            {
                if (IsConforming(archive))
                {
                    source.Position = 0;
                    return Task.FromResult(new PreparedEpub(source, repaired: false));
                }
            }
            catch (InvalidDataException exception)
            {
                throw InvalidEpub("The source is not a valid ZIP archive.", exception);
            }

            CheckSafety(archive);
            var target = EpubTempFile.Create();
            try
            {
                Rebuild(archive, target, cancellation);
                Verify(target);
                target.Position = 0;
                return Task.FromResult(new PreparedEpub(target, repaired: true));
            }
            catch
            {
                target.Dispose();
                throw;
            }
        }
    }

    // The parser's own marker rule: mimetype first, exactly application/epub+zip.
    private static bool IsConforming(ZipArchive archive) =>
        archive.Entries.Count > 0
        && archive.Entries[0].FullName == Marker
        && ReadSmallText(archive.Entries[0], 64) == MarkerValue;

    // Central-directory metadata only; no entry content is decompressed here.
    private void CheckSafety(ZipArchive archive)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (IsUnsafePath(entry.FullName))
            {
                throw InvalidEpub("The archive contains an unsafe entry path.");
            }

            if (!paths.Add(entry.FullName))
            {
                throw InvalidEpub("The archive contains duplicate entry paths.");
            }

            if (((entry.ExternalAttributes >> 16) & 0xF000) == SymbolicLinkMode)
            {
                throw InvalidEpub("The archive contains symbolic links.");
            }

            if (entry.FullName.StartsWith(Marker + "/", StringComparison.Ordinal))
            {
                throw InvalidEpub("The archive contains an invalid mimetype directory.");
            }
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

            total += entry.Length;
            if (total > _options.MaxTotalUncompressedBytes)
            {
                throw InvalidEpub("The EPUB exceeds the configured uncompressed size limit.");
            }
        }
    }

    private static bool IsUnsafePath(string path) =>
        path.StartsWith('/')
        || path.Contains('\\')
        || DriveLetter().IsMatch(path)
        || path.Split('/').Any(segment => segment == "..");

    // Create mode streams entries into the seekable temp file; Update mode would buffer them in memory.
    private void Rebuild(ZipArchive source, Stream target, CancellationToken cancellation)
    {
        try
        {
            using var rebuilt = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true);
            var marker = rebuilt.CreateEntry(Marker, CompressionLevel.NoCompression);
            using (var stream = marker.Open())
            {
                stream.Write(Encoding.ASCII.GetBytes(MarkerValue));
            }

            var buffer = new byte[81920];
            long total = 0;
            foreach (var entry in source.Entries)
            {
                cancellation.ThrowIfCancellationRequested();
                if (entry.FullName == Marker || entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                var copy = rebuilt.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                copy.LastWriteTime = entry.LastWriteTime;
                using var input = entry.Open();
                using var output = copy.Open();
                long written = 0;
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    written += read;
                    total += read;
                    if (written > _options.MaxEntryBytes)
                    {
                        throw InvalidEpub("An EPUB resource exceeds the configured entry size limit.");
                    }

                    if (total > _options.MaxTotalUncompressedBytes)
                    {
                        throw InvalidEpub("The EPUB exceeds the configured uncompressed size limit.");
                    }

                    output.Write(buffer, 0, read);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            throw InvalidEpub("Unable to rebuild the EPUB archive.", exception);
        }
    }

    private static void Verify(Stream target)
    {
        try
        {
            target.Position = 0;
            using var archive = new ZipArchive(target, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count(entry => entry.FullName == Marker) == 1 && IsConforming(archive))
            {
                return;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            throw InvalidEpub("The repaired EPUB failed its integrity check.", exception);
        }

        throw InvalidEpub("The repaired EPUB failed its integrity check.");
    }

    // Same bounded read as the parser's ReadSmallText.
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

    private static EpubParseException InvalidEpub(string message, Exception? inner = null) =>
        new(EpubParseErrorKind.InvalidEpub, message, inner);

    [GeneratedRegex(@"^[A-Za-z]:", RegexOptions.CultureInvariant)]
    private static partial Regex DriveLetter();
}
