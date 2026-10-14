using System.Text.RegularExpressions;

namespace WebApp.Tests.Epub;

// Tests that create EPUB temporary files run one at a time so temp-directory snapshots stay exact.
[CollectionDefinition(Name)]
public sealed class TempFilesCollection
{
    public const string Name = "EPUB temporary files";
}

internal static partial class TempFiles
{
    /// <summary>Files named like Path.GetRandomFileName() in the temp directory.</summary>
    public static HashSet<string> Snapshot() =>
        Directory.EnumerateFiles(Path.GetTempPath())
            .Where(path => RandomName().IsMatch(Path.GetFileName(path)))
            .ToHashSet(StringComparer.Ordinal);

    public static void AssertNoneAdded(HashSet<string> before) =>
        Assert.Empty(Snapshot().Except(before));

    [GeneratedRegex(@"^[a-z0-9]{8}\.[a-z0-9]{3}$")]
    private static partial Regex RandomName();
}
