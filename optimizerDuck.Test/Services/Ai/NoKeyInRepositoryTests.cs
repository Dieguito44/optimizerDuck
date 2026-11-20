using System.IO;
using System.Text.RegularExpressions;

namespace optimizerDuck.Test.Services.Ai;

/// <summary>
///     The repository is public: a real Claude key in any tracked file would be stolen. The key
///     built into Diego's Zentra comes from a DPAPI file outside the repository (20/11/2026). This
///     guard fails if a key-shaped value ever lands in the sources. Test keys are generated at run
///     time, so the only literal allowed is the short fake inside the redactor tests.
/// </summary>
public partial class NoKeyInRepositoryTests
{
    [GeneratedRegex(@"sk-ant-[a-z]+[0-9]*-[A-Za-z0-9_\-]{60,}")]
    private static partial Regex KeyShaped();

    [Fact]
    public void NoTrackedFile_HoldsAKeyShapedValue()
    {
        var root = RepositoryRoot();
        string[] extensions = [".cs", ".xaml", ".resx", ".json", ".csproj", ".props", ".targets", ".ps1", ".yml", ".md", ".txt", ".xml"];
        var offenders = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f =>
                !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")
            )
            .Where(f =>
                KeyShaped()
                    .Matches(File.ReadAllText(f))
                    .Any(m => !m.Value.Contains("ABCDEFGHIJKLMNOPQRSTUVWXYZ")) // the redactor tests' fake
            )
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.Empty(offenders);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "optimizerDuck.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
