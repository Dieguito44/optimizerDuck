using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace optimizerDuck.Services.Cli;

/// <summary>
///     Where the CLI writes. Zentra is a windowed app, so it has no console of its own: output sent to
///     a file or a pipe is used as it is; otherwise it attaches to the console that started it. With
///     neither (started from a shortcut), the text is lost here but kept in zentra-cli.txt.
/// </summary>
internal static class CliConsole
{
    private const int StdOutput = -11;
    private const int AttachParentProcess = -1;
    private const uint FileTypeDisk = 1;
    private const uint FileTypePipe = 3;

    public static TextWriter Open()
    {
        if (IsRedirected())
            return new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };

        if (!AttachConsole(AttachParentProcess))
            return TextWriter.Null;

        // The console keeps its own code page (often 850 in Spanish): write in it, so the accents
        // come out right without changing the technician's console.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding((int)GetConsoleOutputCP());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            encoding = new UTF8Encoding(false);
        }

        var writer = new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true };
        // cmd has already printed its prompt (it does not wait for windowed apps): start on a new line.
        writer.WriteLine();
        return writer;
    }

    private static bool IsRedirected()
    {
        var handle = GetStdHandle(StdOutput);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return false;
        var type = GetFileType(handle);
        return type is FileTypeDisk or FileTypePipe;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();
}
