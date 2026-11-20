using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using optimizerDuck.Common.Extensions;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Configuration;
using optimizerDuck.Resources.Languages;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Customize;
using optimizerDuck.Services.Optimization;
using optimizerDuck.Services.Revert;
using optimizerDuck.Services.System;
using optimizerDuck.Services.System.Primitives;
using optimizerDuck.Services.UI;
using optimizerDuck.UI.Behaviors;
using optimizerDuck.UI.Pages;
using optimizerDuck.UI.ViewModels.Pages;
using optimizerDuck.UI.ViewModels.Windows;
using optimizerDuck.UI.Windows;
using Serilog;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Parsing;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.DependencyInjection;
using Wpf.Ui.Extensions;

namespace optimizerDuck;

public class ScopeBlockTextFormatter : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        // Render first, then redact: no log line may carry an API key, whatever wrote it.
        using var buffer = new StringWriter();
        FormatCore(logEvent, buffer);
        output.Write(SecretRedactor.Redact(buffer.ToString()));
    }

    private static void FormatCore(LogEvent logEvent, TextWriter output)
    {
        var timestamp = $"{logEvent.Timestamp:yyyy-MM-dd HH:mm:ss}";
        var ctx = logEvent.Properties.TryGetValue("SourceContext", out var sourceContext)
            ? sourceContext is ScalarValue { Value: string s }
                ? s.Split(".")[^1]
                : sourceContext.ToString().Split(".")[^1]
            : "";
        var levelText = logEvent.Level switch
        {
            LogEventLevel.Verbose => "VERBOSE",
            LogEventLevel.Debug => "DEBUG",
            LogEventLevel.Information => "INFO",
            LogEventLevel.Warning => "WARNING",
            LogEventLevel.Error => "ERROR",
            LogEventLevel.Fatal => "FATAL",
            _ => "UNKNOWN",
        };

        //var prefix = $"{timestamp} | {ctx,-67} | {levelText,-7} | "; // byebye 67 char SourceContext truncation, we have a new design now...
        var prefix = $"{timestamp} | {ctx, -35} | {levelText, -7} | ";

        output.WriteLine(prefix + RenderWithoutQuotes(logEvent));

        foreach (var kvp in logEvent.Properties)
        {
            if (kvp.Key == "SourceContext")
                continue;

            var usedInMessage = logEvent
                .MessageTemplate.Tokens.OfType<PropertyToken>()
                .Any(t => t.PropertyName == kvp.Key);
            if (usedInMessage)
                continue;

            var valueText = kvp.Value is ScalarValue sv
                ? sv.Value?.ToString() ?? ""
                : kvp.Value.ToString();

            output.WriteLine(new string(' ', prefix.Length) + $"{kvp.Key} = {valueText}");
        }

        if (logEvent.Exception != null)
        {
            output.WriteLine("Exception : ");
            output.WriteLine($"    {logEvent.Exception}");
        }
    }

    private static string RenderWithoutQuotes(LogEvent logEvent)
    {
        var output = new StringWriter();
        foreach (var token in logEvent.MessageTemplate.Tokens)
            if (
                token is PropertyToken pt
                && logEvent.Properties.TryGetValue(pt.PropertyName, out var value)
            )
            {
                if (value is ScalarValue { Value: string s })
                    output.Write(s);
                else
                    pt.Render(logEvent.Properties, output);
            }
            else
            {
                token.Render(logEvent.Properties, output);
            }

        return output.ToString();
    }
}

public partial class App : Application
{
    private IHost? _host;

    public IHost? AppHost => _host;
    private ILogger<App> _logger = null!;
    private bool _allowClose;
    private IContentDialogService? _contentDialogService = null;

    private bool _hasPendingChanges;

    public bool HasPendingChanges
    {
        get => _hasPendingChanges;
        set
        {
            if (_hasPendingChanges == value)
                return;
            _hasPendingChanges = value;
            Dispatcher.InvokeAsync(() =>
            {
                if (Current.MainWindow is MainWindow mw)
                    mw.UpdatePendingIndicator(value);
            });
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // CLI mode (plan F3-02) opens no main window: only Shutdown ends it.
        if (Services.Cli.CliOptions.IsCliInvocation(e.Args))
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _ = SafeFireAndForgetAsync(() => OnStartupAsync(e));
    }

    /// <summary>
    ///     Applies Zentra's palette (the logo's teal over slate surfaces) for the given theme.
    ///     Centralized so startup and theme switches always use the same palette.
    /// </summary>
    internal static void ApplyAccentFor(ApplicationTheme theme) =>
        UI.Styles.ZentraTheme.Apply(theme);

    private static async Task SafeFireAndForgetAsync(Func<Task> taskFactory)
    {
        try
        {
            await taskFactory();
        }
        catch (Exception ex)
        {
            try
            {
                await HandleStartupErrorStaticAsync(ex);
            }
            catch { }
        }
    }

    private static async Task HandleStartupErrorStaticAsync(Exception ex)
    {
        LogExceptionToFile("App.Startup", ex);

        try
        {
            Log.Logger?.Fatal(ex, "Fatal error during startup");
            await Log.CloseAndFlushAsync();
        }
        catch
        {
            // ignore logging failures during fatal startup handling
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            System.Windows.MessageBox.Show(
                $"No se pudo iniciar {Shared.AppName}.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                Shared.AppName,
                System.Windows.MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            Application.Current.Shutdown(-1);
        });
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString());
        LogExceptionToFile("AppDomain.UnhandledException", ex);
        Log.Logger?.Fatal(
            ex,
            "Unhandled AppDomain exception. IsTerminating={IsTerminating}",
            e.IsTerminating
        );
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogExceptionToFile("UnobservedTaskException", e.Exception);
        Log.Logger?.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e
    )
    {
        LogExceptionToFile("DispatcherUnhandledException", e.Exception);
        Log.Logger?.Error(e.Exception, "Unhandled UI dispatcher exception");

        // The exception is swallowed below, so the user is told about it here.
        try
        {
            _host?.Services.GetService<UserErrorSurface>()?.TryReport(e.Exception);
        }
        catch (Exception ex)
        {
            Log.Logger?.Warning(ex, "Could not report the unhandled exception to the user");
        }

        e.Handled = true;
    }

    private static void LogExceptionToFile(string category, Exception ex)
    {
        try
        {
            var crashDir = Path.Combine(Shared.RootDirectory, "Crashes");
            Directory.CreateDirectory(crashDir);
            var crashFile = Path.Combine(
                crashDir,
                $"crash_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.log"
            );
            File.WriteAllText(crashFile, CrashText(category, ex));
            // Plan F4-05: also a report without personal data, only if the technician turned it on.
            Services.Configuration.CrashReport.TryWrite(category, ex);
        }
        catch
        {
            // cannot do anything if crash logging itself fails
        }
    }

    /// <summary>The crash file text, redacted so an exception can never write an API key to disk.</summary>
    internal static string CrashText(string category, Exception ex) =>
        SecretRedactor.Redact(
            $"""
            [{category}] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}
            Type: {ex.GetType().FullName}
            Message: {ex.Message}
            StackTrace:
            {ex.StackTrace}

            {(ex.InnerException is not null ? $"Inner: {ex.InnerException}" : "")}
            """
        );

    /// <summary>
    ///     Plan of improvements F1-11: Information by default; Verbose only on demand
    ///     (App.VerboseLogs in appsettings.json, or ZENTRA_LOG_VERBOSE=1).
    /// </summary>
    internal static readonly Serilog.Core.LoggingLevelSwitch LogLevel = new(
        Environment.GetEnvironmentVariable("ZENTRA_LOG_VERBOSE") == "1"
            ? LogEventLevel.Verbose
            : LogEventLevel.Information
    );

    /// <summary>Each log file up to 5 MB; 10 files kept at most, so 50 MB in all.</summary>
    internal const long LogFileBytes = 5 * 1024 * 1024;

    internal const int LogFilesKept = 10;

    /// <summary>The app's log configuration. Shared with the key-leak test so it checks the real one.</summary>
    internal static LoggerConfiguration BuildLoggerConfiguration(string logPath) =>
        new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LogLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            // Daily files, rolled earlier at 5 MB, the oldest deleted after 10 (plan F1-11).
            // Secrets are redacted by the formatter in every line.
            .WriteTo.File(
                new ScopeBlockTextFormatter(),
                logPath,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: LogFileBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: LogFilesKept
            );

    private async Task OnStartupAsync(StartupEventArgs e)
    {
        Directory.CreateDirectory(Shared.RootDirectory);
        Directory.CreateDirectory(Shared.ResourcesDirectory);
        Directory.CreateDirectory(Shared.DownloadsDirectory);
        Directory.CreateDirectory(Shared.AssetsDirectory);

        var logPath = Path.Combine(Shared.RootDirectory, $"{Shared.AppName}.log");
        Log.Logger = BuildLoggerConfiguration(logPath).CreateLogger();

        // Plan F1-02: the revert state in a folder only administrators can write. If it cannot
        // be made safe, nothing may be applied: Zentra stops here.
        if (!Services.State.StateStore.Prepare())
        {
            await Dispatcher.InvokeAsync(() =>
            {
                System.Windows.MessageBox.Show(
                    Loc.Instance["State.Unsafe", Shared.StateDirectory],
                    Shared.AppName,
                    System.Windows.MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                Shutdown((int)Services.Cli.CliExitCode.Failed);
            });
            return;
        }

        _host = Microsoft
            .Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureAppConfiguration(c =>
            {
                using var loggerFactory = LoggerFactory.Create(b => b.AddSerilog(Log.Logger));
                ConfigManager.ValidateConfig(loggerFactory.CreateLogger(typeof(ConfigManager)));
                c.AddJsonFile(Path.Combine(Shared.RootDirectory, "appsettings.json"), false, true);
            })
            // Fail fast instead of discovering a broken graph later: ValidateOnBuild catches
            // an unbuildable registration, ValidateScopes catches a scoped service leaking
            // into a singleton (the ChangeSet must never be resolved from the root provider).
            .UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            })
            .ConfigureServices(
                (context, services) => services.AddOptimizerApplication(context.Configuration)
            )
            .Build();

        await _host.StartAsync();

        // Plan F1-03: a run that did not finish left its changes in the journal; they become
        // revert data now, before anything else can apply.
        var recovered = await Services.Revert.IntentJournal.RecoverAsync(
            _host.Services.GetRequiredService<RevertManager>()
        );

        // ACT-MON-HZ asks the person at the PC before keeping a new refresh rate.
        optimizerDuck.Services.Diagnostics.DisplayModeService.Confirm =
            DisplayConfirmWindow.AskAsync;

        // Phase 7 options need the client's written consent.
        ClientConsent.Ask = ConsentWindow.AskAsync;

        var config = _host.Services.GetRequiredService<ConfigManager>();
        await config.InitializeAsync();
        await config.EnsureDefaultsAsync();

        var appOptionsMonitor = _host.Services.GetRequiredService<IOptionsMonitor<AppSettings>>();

        // init WMI helper for cleanup on abnormal termination
        WmiHelper.Initialize();

        var appSettings = appOptionsMonitor.CurrentValue;
        Services.Configuration.CrashReport.Enabled = appSettings.App.CrashReports;
        if (appSettings.App.VerboseLogs)
            LogLevel.MinimumLevel = LogEventLevel.Verbose;

        SmoothScrollBehavior.GlobalEnabled = appSettings.Optimize.SmoothScrolling;

        await Dispatcher.InvokeAsync(() =>
        {
            Loc.Instance.ChangeCulture(
                Domain.UI.SupportedLanguages.Resolve(appSettings.App.Language).Culture
            );
        });

        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation(
            "\n{Logo}\nVersion: {Version}\n\n",
            Shared.RawLogo,
            Shared.FileVersion
        );
        _logger.LogInformation("Loaded language: {Language}", appSettings.App.Language);

        var optimizationRegistry = _host.Services.GetRequiredService<OptimizationRegistry>();

        if (Services.Cli.CliOptions.IsCliInvocation(e.Args))
        {
            await RunCliAsync(e.Args);
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            // Theme first: ApplyAccentFor reads GetAppTheme() cache to pick
            // AccentFillColorDefault (secondary for Dark, primary for Light).
            ApplicationThemeManager.Apply(
                appSettings.App.Theme switch
                {
                    ApplicationTheme.Dark => ApplicationTheme.Dark,
                    ApplicationTheme.HighContrast => ApplicationTheme.HighContrast,
                    _ => ApplicationTheme.Light,
                },
                updateAccent: false
            );
            ApplyAccentFor(appSettings.App.Theme);

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Closing += MainWindow_Closing;
            mainWindow.Show();

            if (recovered.Count > 0)
                System.Windows.MessageBox.Show(
                    mainWindow,
                    Loc.Instance[
                        "State.Recovered",
                        string.Join(
                            Environment.NewLine,
                            recovered.Select(r => $"• {r.Name} ({r.Steps.Count})")
                        )
                    ],
                    Shared.AppName,
                    System.Windows.MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
        });

        _logger.LogInformation("Preloading optimizations and customize settings...");
        var customizeRegistry = _host.Services.GetRequiredService<CustomizeRegistry>();
        await Task.WhenAll(
                optimizationRegistry.PreloadOptimizationsAsync(),
                customizeRegistry.PreloadCategoriesAsync()
            )
            .ConfigureAwait(false);

        RevertManager.RemoveOrphanedTempFiles(_logger);
    }

    /// <summary>
    ///     Plan of improvements F3-02: runs the command line instead of the window and exits with its
    ///     code. Arguments are logged; the client label is not (it stays in session.json only).
    /// </summary>
    private async Task RunCliAsync(string[] args)
    {
        var output = Services.Cli.CliConsole.Open();
        var options = Services.Cli.CliOptions.Parse(args, out var error);
        Services.Cli.CliExitCode code;
        if (options is null)
        {
            _logger?.LogWarning("CLI arguments refused: {Error}", error);
            output.WriteLine(Loc.Instance["Cli.BadArguments", error ?? ""]);
            output.WriteLine(Loc.Instance["Cli.Help"]);
            code = Services.Cli.CliExitCode.BadArguments;
        }
        else
        {
            _logger?.LogInformation(
                "CLI run: preset={Preset} diagnose={Diagnose} report={Report} silent={Silent} allowRisky={Risky} keepApps={KeepApps}",
                options.PresetPath,
                options.Diagnose,
                options.ReportDirectory,
                options.Silent,
                options.AllowRisky,
                options.KeepApps
            );
            var runner = new Services.Cli.CliRunner(
                _host!.Services.GetRequiredService<Services.Profiles.ServiceProfileRunner>(),
                _host.Services.GetRequiredService<Services.Cli.ICliHost>(),
                output,
                _host.Services.GetRequiredService<ILogger<Services.Cli.CliRunner>>()
            );
            code = await runner.RunAsync(options, CancellationToken.None);
        }

        _logger?.LogInformation("CLI finished: {Code} ({Value})", code, (int)code);
        await output.FlushAsync();
        await Dispatcher.InvokeAsync(() => Shutdown((int)code));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host != null)
            {
                // The key lives only in memory; drop it before anything else on exit.
                _host.Services.GetService<optimizerDuck.Services.Ai.ApiKeyStore>()?.Clear();
                try
                {
                    _host.StopAsync().Wait(TimeSpan.FromSeconds(2));
                }
                catch (Exception ex)
                {
                    LogExceptionToFile("App.OnExit.StopAsync", ex);
                }
                _host.Dispose();
            }

            WmiHelper.DisposeScopes();
            Log.CloseAndFlush();
        }
        catch (Exception ex)
        {
            LogExceptionToFile("App.OnExit", ex);
        }

        base.OnExit(e);
    }

    protected async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            if (_allowClose)
            {
                return;
            }

            if (!HasPendingChanges)
            {
                return;
            }

            e.Cancel = true;

            _contentDialogService ??= _host!.Services.GetRequiredService<IContentDialogService>();

            var result = await _contentDialogService.ShowSimpleDialogAsync(
                new SimpleContentDialogCreateOptions
                {
                    Title = Loc.Instance["Dialog.PendingChanges.Title"],
                    Content = Loc.Instance["Dialog.PendingChanges.Content"],
                    CloseButtonText = Loc.Instance["Dialog.PendingChanges.CloseButton"],
                    PrimaryButtonText = Loc.Instance["Dialog.PendingChanges.PrimaryButton"],
                    SecondaryButtonText = Loc.Instance["Dialog.PendingChanges.SecondaryButton"],
                }
            );

            switch (result)
            {
                case ContentDialogResult.Primary:
                    _logger.LogInformation("User chose to restart PC.");

                    await _host!
                        .Services.GetRequiredService<ShellService>()
                        .QueryCMDAsync("shutdown /r /t 0");
                    _allowClose = true;
                    Current.Shutdown();
                    break;

                case ContentDialogResult.Secondary:
                    _logger.LogInformation("User chose to restart Explorer.");

                    await _host!
                        .Services.GetRequiredService<ShellService>()
                        .QueryCMDAsync("taskkill /f /im explorer.exe && start explorer.exe");
                    _allowClose = true;
                    Current.Shutdown();
                    break;

                case ContentDialogResult.None:
                    _logger.LogInformation("User chose to exit without applying changes.");

                    _allowClose = true;
                    Current.Shutdown();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to handle window closing event. Allowing close to prevent hanging."
            );

            // Ensure we don't block close on error
            _allowClose = true;
            e.Cancel = false;
        }
    }
}
