using System.IO;
using System.IO.Enumeration;

namespace optimizerDuck.Services.UI;

/// <summary>
///     Plan of improvements F1-06 (audit Z-03): the cleanup runs elevated, and .NET's recursive
///     enumeration follows junctions and symbolic links. A link planted inside %TEMP% would make
///     it delete files elsewhere. This walk never goes down a reparse point, never returns one,
///     and counts what it skipped so the cleanup can say so.
/// </summary>
internal static class SafeTreeWalk
{
    /// <summary>True when the folder itself is a junction or a link (then it is not cleaned at all).</summary>
    public static bool IsLink(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists
                && (
                    info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || info.LinkTarget is not null
                );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // unreadable: treated as unsafe
        }
    }

    /// <summary>Files under <paramref name="root" /> that match the pattern, without crossing links.</summary>
    public static IEnumerable<FileInfo> Files(
        string root,
        string pattern,
        bool recurse,
        Action onLinkSkipped
    ) =>
        new FileSystemEnumerable<FileInfo>(
            root,
            static (ref FileSystemEntry e) => (FileInfo)e.ToFileSystemInfo(),
            Options(recurse)
        )
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
            {
                if (IsReparse(ref e))
                {
                    onLinkSkipped();
                    return false;
                }
                return !e.IsDirectory
                    && FileSystemName.MatchesSimpleExpression(pattern, e.FileName);
            },
            ShouldRecursePredicate = (ref FileSystemEntry e) => !IsReparse(ref e),
        };

    /// <summary>Real subdirectories (no links), deepest first, to remove the empty ones.</summary>
    public static IReadOnlyList<DirectoryInfo> Directories(string root, Action onLinkSkipped) =>
        new FileSystemEnumerable<DirectoryInfo>(
            root,
            static (ref FileSystemEntry e) => (DirectoryInfo)e.ToFileSystemInfo(),
            Options(recurse: true)
        )
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
            {
                if (!e.IsDirectory)
                    return false;
                if (IsReparse(ref e))
                {
                    onLinkSkipped();
                    return false;
                }
                return true;
            },
            ShouldRecursePredicate = (ref FileSystemEntry e) => !IsReparse(ref e),
        }
            .OrderByDescending(d => d.FullName.Length)
            .ToList();

    private static bool IsReparse(ref FileSystemEntry e) =>
        (e.Attributes & FileAttributes.ReparsePoint) != 0;

    private static EnumerationOptions Options(bool recurse) =>
        new()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = recurse,
            ReturnSpecialDirectories = false,
            // The defaults (skip Hidden and System) are kept, as the cleanup had them.
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
}
