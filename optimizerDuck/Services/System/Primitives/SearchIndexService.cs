using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace optimizerDuck.Services.System.Primitives;

/// <summary>
///     Windows Search crawl scope through its COM interface (ISearchCrawlScopeManager), the way
///     Indexing Options excludes a folder (plan phase 6B, ACT-INDEX-EXCLUDE). Interface layouts
///     follow Microsoft.Search.Interop (Windows SDK searchapi.h); unused slots are placeholders.
/// </summary>
public static partial class SearchIndexService
{
    [ComImport, Guid("7D096C5F-AC08-4F1F-BEB7-5C22C517CE39")]
    private class CSearchManager;

    [
        ComImport,
        Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF69"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface ISearchManager
    {
        void GetIndexerVersionStr();
        void GetIndexerVersion();
        void GetParameter();
        void SetParameter();
        void GetProxyName();
        void GetBypassList();
        void SetProxy();

        [return: MarshalAs(UnmanagedType.Interface)]
        ISearchCatalogManager GetCatalog([MarshalAs(UnmanagedType.LPWStr)] string catalog);
    }

    [
        ComImport,
        Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF50"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface ISearchCatalogManager
    {
        void GetName();
        void GetParameter();
        void SetParameter();
        void GetCatalogStatus();
        void Reset();
        void Reindex();
        void ReindexMatchingUrls();
        void ReindexSearchRoot();
        void PutConnectTimeout();
        void GetConnectTimeout();
        void PutDataTimeout();
        void GetDataTimeout();
        void NumberOfItems();
        void NumberOfItemsToIndex();
        void UrlBeingIndexed();
        void GetUrlIndexingState();
        void GetPersistentItemsChangedSink();
        void RegisterViewForNotification();
        void GetItemsChangedSink();
        void UnregisterViewForNotification();
        void SetExtensionClusion();
        void EnumerateExcludedExtensions();
        void GetQueryHelper();
        void PutDiacriticSensitivity();
        void GetDiacriticSensitivity();

        [return: MarshalAs(UnmanagedType.Interface)]
        ISearchCrawlScopeManager GetCrawlScopeManager();
    }

    [
        ComImport,
        Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF55"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface ISearchCrawlScopeManager
    {
        void AddDefaultScopeRule();
        void AddRoot();
        void RemoveRoot();
        void EnumerateRoots();
        void AddHierarchicalScope();

        void AddUserScopeRule(
            [MarshalAs(UnmanagedType.LPWStr)] string url,
            [MarshalAs(UnmanagedType.Bool)] bool include,
            [MarshalAs(UnmanagedType.Bool)] bool overrideChildren,
            uint followFlags
        );

        void RemoveScopeRule([MarshalAs(UnmanagedType.LPWStr)] string rule);
        void EnumerateScopeRules();
        void HasParentScopeRule();
        void HasChildScopeRule();

        [return: MarshalAs(UnmanagedType.Bool)]
        bool IncludedInCrawlScope([MarshalAs(UnmanagedType.LPWStr)] string url);

        void IncludedInCrawlScopeEx();
        void RevertToDefaultScopes();
        void SaveAll();
    }

    /// <summary>The scope rule for a folder and everything under it.</summary>
    public static string RuleFor(string folder) => $"file:///{folder.TrimEnd('\\')}\\*";

    private static string UrlFor(string folder) => $"file:///{folder.TrimEnd('\\')}\\";

    private static ISearchCrawlScopeManager Scope() =>
        ((ISearchManager)new CSearchManager()).GetCatalog("SystemIndex").GetCrawlScopeManager();

    public static bool IsIndexed(string folder) => Scope().IncludedInCrawlScope(UrlFor(folder));

    /// <summary>Excludes a folder from the index. True when Windows reports it out of scope after.</summary>
    public static bool Exclude(string folder)
    {
        var scope = Scope();
        scope.AddUserScopeRule(RuleFor(folder), false, true, 0);
        scope.SaveAll();
        return !Scope().IncludedInCrawlScope(UrlFor(folder));
    }

    /// <summary>Removes the exclusion Zentra added, so the folder follows its parent again.</summary>
    public static void RemoveExclusion(string folder)
    {
        var scope = Scope();
        scope.RemoveScopeRule(RuleFor(folder));
        scope.SaveAll();
    }

    /// <summary>
    ///     Folders that are big and rarely searched by name: the user's Downloads, Steam library
    ///     game folders and Epic Games install folders. Only existing folders.
    /// </summary>
    public static IReadOnlyList<string> LargeFolders()
    {
        var folders = new List<string>();

        using (
            var shell = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"
            )
        )
            if (shell?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string downloads)
                folders.Add(Environment.ExpandEnvironmentVariables(downloads));

        using (var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            if (steam?.GetValue("SteamPath") is string steamPath)
            {
                var vdf = Path.Combine(
                    steamPath.Replace('/', '\\'),
                    "steamapps",
                    "libraryfolders.vdf"
                );
                if (File.Exists(vdf))
                    foreach (Match m in VdfPath().Matches(File.ReadAllText(vdf)))
                        folders.Add(
                            Path.Combine(
                                m.Groups[1].Value.Replace(@"\\", @"\"),
                                "steamapps",
                                "common"
                            )
                        );
            }

        var epic = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Epic\EpicGamesLauncher\Data\Manifests"
        );
        if (Directory.Exists(epic))
            foreach (var item in Directory.EnumerateFiles(epic, "*.item"))
                try
                {
                    if (
                        JObject.Parse(File.ReadAllText(item)).Value<string>("InstallLocation") is
                        { Length: > 0 } dir
                    )
                        folders.Add(dir);
                }
                catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException) { }

        return folders
            .Where(Directory.Exists)
            .Select(f => Path.GetFullPath(f).TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfPath();
}
