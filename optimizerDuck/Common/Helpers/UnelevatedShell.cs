using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace optimizerDuck.Common.Helpers;

/// <summary>
///     Opens web pages, PDFs, folders, files and Settings as the signed-in user, not as the
///     administrator Zentra runs as (plan of improvements F2-06, audit Z-30).
/// </summary>
/// <remarks>
///     <para>
///         <c>Process.Start</c> with <c>UseShellExecute</c> from an elevated process starts the
///         browser or the PDF viewer elevated too, and picks the program from the user's own
///         file associations (HKCU), which any program of that user can change: the same idea
///         as the known "fodhelper" elevation bypass.
///     </para>
///     <para>
///         Here the target goes to <c>explorer.exe</c> from the Windows folder instead. A new
///         explorer.exe hands the request to the desktop shell that is already running, as the
///         signed-in user, and exits; the page or file opens there, without elevation.
///         System tools that need administrator rights (Task Manager, Task Scheduler, Control
///         Panel) are not opened through here.
///     </para>
/// </remarks>
public static class UnelevatedShell
{
    /// <summary>Opens a URL (http, https, ms-settings), a file or a folder.</summary>
    /// <exception cref="ArgumentException">The target is empty or would break the command line.</exception>
    public static void Open(string target) => Start(Validate(target), select: false);

    /// <summary>Opens the folder of a file in Explorer with that file selected.</summary>
    public static void Select(string filePath) => Start(Validate(filePath), select: true);

    /// <summary>
    ///     Quotes, control characters and line breaks never belong in a URL or a path and would
    ///     let the target add its own arguments to explorer.exe.
    /// </summary>
    internal static string Validate(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("Nothing to open.", nameof(target));
        if (target.Any(c => c == '"' || char.IsControl(c)))
            throw new ArgumentException(
                "The target contains characters that cannot be opened.",
                nameof(target)
            );
        return target.Trim();
    }

    internal static string Arguments(string target, bool select) =>
        select ? $"/select,\"{target}\"" : $"\"{target}\"";

    private static void Start(string target, bool select)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            // Not elevated (tests, development): the normal shell call is already the user's.
            Process.Start(
                select
                    ? new ProcessStartInfo(
                        Services.System.SystemTools.Resolve("explorer"),
                        Arguments(target, select: true)
                    )
                    : new ProcessStartInfo(target) { UseShellExecute = true }
            );
            return;
        }

        // The desktop shell itself runs the request, as the signed-in user. Handing a folder to
        // a new explorer.exe from an elevated process fails on Windows 11 with "Location is not
        // available", so explorer.exe is only the fallback.
        var explorer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe"
        );
        if (
            select
                ? DesktopShell.TryShellExecute(explorer, Arguments(target, select: true))
                : DesktopShell.TryShellExecute(target, null)
        )
            return;

        Process.Start(
            new ProcessStartInfo
            {
                FileName = explorer,
                Arguments = Arguments(target, select),
                UseShellExecute = false,
                WorkingDirectory = Environment.SystemDirectory,
            }
        );
    }
}

/// <summary>
///     ShellExecute through the desktop's own shell (the documented way to start something
///     unelevated from an elevated process): find the desktop window among the shell windows,
///     ask its browser for the active view, take the view's automation object and call
///     IShellDispatch2.ShellExecute on it. Everything runs inside the user's explorer.exe.
/// </summary>
internal static class DesktopShell
{
    private const int CsidlDesktop = 0;
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private const uint SvgioBackground = 0;

    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IidDispatch = new("00020400-0000-0000-C000-000000000046");

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(in Guid service, in Guid riid);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();
        IShellView QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void TranslateAccelerator();
        void EnableModeless();
        void UIActivate();
        void Refresh();
        void CreateViewWindow();
        void DestroyViewWindow();
        void GetCurrentInfo();
        void AddPropertySheetPages();
        void SaveViewState();
        void SelectItem();

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object GetItemObject(uint item, in Guid riid);
    }

    /// <summary>Runs "file arguments" through the desktop shell; false when that is not possible.</summary>
    public static bool TryShellExecute(string file, string? arguments)
    {
        object? shellWindows = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidShellWindows);
            if (type is null)
                return false;
            shellWindows = Activator.CreateInstance(type);
            if (shellWindows is null)
                return false;

            dynamic windows = shellWindows;
            object location = CsidlDesktop;
            object empty = Type.Missing;
            int hwnd = 0;
            object desktop = windows.FindWindowSW(
                ref location,
                ref empty,
                SwcDesktop,
                ref hwnd,
                SwfoNeedDispatch
            );

            var browser = (IShellBrowser)
                ((IServiceProvider)desktop).QueryService(SidTopLevelBrowser, IidShellBrowser);
            var view = browser.QueryActiveShellView();
            dynamic folderView = view.GetItemObject(SvgioBackground, IidDispatch);
            dynamic shell = folderView.Application;
            shell.ShellExecute(file, arguments ?? string.Empty, string.Empty, "open", 1);
            return true;
        }
        catch (Exception ex)
            when (ex
                    is COMException
                        or InvalidCastException
                        or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                        or ArgumentException
            )
        {
            return false;
        }
        finally
        {
            if (shellWindows is not null && Marshal.IsComObject(shellWindows))
                Marshal.ReleaseComObject(shellWindows);
        }
    }
}
