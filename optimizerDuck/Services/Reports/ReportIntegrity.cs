using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace optimizerDuck.Services.Reports;

public enum IntegrityStatus
{
    Ok,
    Altered,
    Missing,
}

public sealed record IntegrityCheck(string File, IntegrityStatus Status);

/// <summary>
///     Plan of improvements F3-04: SHA-256 fingerprints of what the client receives (the PDF and
///     session.json), in a ZENTRA-SHA256.txt next to them, in the same format as sha256sum. The
///     file alone proves nothing (whoever changes the PDF can change it too): the PDF fingerprint
///     is shown to the technician to hand over separately, and the PDF prints the fingerprint of
///     the session.json it was made from. Code signing (plan F1-10) can sign them later.
/// </summary>
public static class ReportIntegrity
{
    public const string ManifestName = "ZENTRA-SHA256.txt";

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Writes the manifest for the given files (all in <paramref name="folder" />) and returns its path.</summary>
    public static string WriteManifest(string folder, IEnumerable<string> files)
    {
        var text = new StringBuilder()
            .AppendLine("# Zentra: SHA-256 de los archivos del informe.")
            .AppendLine(
                "# Verificar: Zentra.exe --verify <carpeta>, o en PowerShell: Get-FileHash <archivo> -Algorithm SHA256"
            );
        foreach (var file in files.Where(File.Exists))
            text.Append(Sha256Of(file)).Append("  ").AppendLine(Path.GetFileName(file));

        var path = Path.Combine(folder, ManifestName);
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    ///     Checks every file the manifest names. Accepts the folder or the manifest itself. Only bare
    ///     file names next to the manifest are read: a line with a path is "altered".
    /// </summary>
    public static IReadOnlyList<IntegrityCheck>? Verify(string folderOrManifest)
    {
        var manifest = Directory.Exists(folderOrManifest)
            ? Path.Combine(folderOrManifest, ManifestName)
            : folderOrManifest;
        if (!File.Exists(manifest))
            return null;
        var folder = Path.GetDirectoryName(Path.GetFullPath(manifest))!;

        var checks = new List<IntegrityCheck>();
        foreach (var raw in File.ReadAllLines(manifest))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var cut = line.IndexOf("  ", StringComparison.Ordinal);
            if (cut != 64)
            {
                checks.Add(new(line, IntegrityStatus.Altered));
                continue;
            }
            var expected = line[..64];
            var name = line[(cut + 2)..].TrimStart('*');
            if (name.Length == 0 || name != Path.GetFileName(name) || name is "." or "..")
            {
                checks.Add(new(name, IntegrityStatus.Altered));
                continue;
            }

            var path = Path.Combine(folder, name);
            checks.Add(
                !File.Exists(path) ? new(name, IntegrityStatus.Missing)
                : string.Equals(Sha256Of(path), expected, StringComparison.OrdinalIgnoreCase)
                    ? new(name, IntegrityStatus.Ok)
                : new(name, IntegrityStatus.Altered)
            );
        }
        return checks;
    }
}
