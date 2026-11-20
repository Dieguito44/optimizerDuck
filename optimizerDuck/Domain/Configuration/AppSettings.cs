using Wpf.Ui.Appearance;

namespace optimizerDuck.Domain.Configuration;

/// <summary>
///     Application settings that can be persisted to disk.
/// </summary>
public sealed class AppSettings
{
    public AppOptions App { get; set; } = new();

    public OptimizeOptions Optimize { get; set; } = new();

    public BloatwareOptions Bloatware { get; set; } = new();

    public sealed class AppOptions
    {
        /// <summary>
        ///     The UI language code (e.g., "es-CO"). Zentra defaults to Colombian Spanish (F3-11).
        /// </summary>
        public string Language { get; set; } = "es-CO";

        /// <summary>
        ///     The UI theme (e.g., "Dark").
        /// </summary>
        public ApplicationTheme Theme { get; set; } = ApplicationTheme.Dark;

        public bool LegalAccepted { get; set; } = false;

        /// <summary>Plan of improvements F4-05: redacted crash reports. Off by default.</summary>
        public bool CrashReports { get; set; } = false;

        /// <summary>Plan of improvements F1-11: Verbose logs, only when someone needs them.</summary>
        public bool VerboseLogs { get; set; } = false;
    }

    public sealed class OptimizeOptions
    {
        public int ShellTimeoutMs { get; set; } = 120000;

        /// <summary>
        ///     Whether to show the success snackbar after applying an optimization.
        /// </summary>
        public bool ShowCompletionNotification { get; set; } = false;

        /// <summary>
        ///     Whether smooth scrolling is enabled globally.
        /// </summary>
        public bool SmoothScrolling { get; set; } = false;
    }

    public sealed class BloatwareOptions
    {
        /// <summary>
        ///     Whether to remove provisioned AppX packages.
        /// </summary>
        public bool RemoveProvisioned { get; set; } = true;
    }
}
