using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PdfStampNgrokDesktop.Helpers;
using PdfStampNgrokDesktop.Options;
using PdfStampNgrokDesktop.Services;
using PdfStampNgrokDesktop.ViewModels;
using Serilog;

namespace PdfStampNgrokDesktop;

public partial class App : Application
{
    private const string SupportContactFileName = "support-contact.txt";

    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ConfigureLogging();
        RegisterGlobalExceptionHandlers();

        try
        {
            var serviceCollection = new ServiceCollection();
            serviceCollection.Configure<AppRuntimeOptions>(options =>
            {
                options.RefreshIntervalSeconds = 5;
                options.ConfigBackupKeepCount = 30;
                options.LogRetentionFileCount = 14;
                options.TokenRevealSeconds = 20;
                options.MemoryWarningMb = 700;
                options.ShowDeveloperGoogleSheetPanel = ResolveDeveloperModeFlag();
            });

            serviceCollection.AddSingleton<ITokenStoreService, TokenStoreService>();
            serviceCollection.AddSingleton<IBackendService, BackendService>();
            serviceCollection.AddSingleton<INgrokService, NgrokService>();
            serviceCollection.AddSingleton<IHealthMonitorService, HealthMonitorService>();
            serviceCollection.AddSingleton<IUpdateService, UpdateService>();

            serviceCollection.AddSingleton<MainViewModel>();
            serviceCollection.AddSingleton<MainWindow>();

            _services = serviceCollection.BuildServiceProvider();

            var window = _services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed.");
            MessageBox.Show(
                UiText.Format(
                    "StartupErrorMessageTemplate",
                    "Ung dung khong khoi dong duoc.\nXem log tai %APPDATA%\\PdfStampNgrokDesktop\\logs.\n{0}",
                    GetSupportContactText()),
                UiText.Get("StartupErrorTitle", "Loi khoi dong"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        (_services as IDisposable)?.Dispose();
        Log.CloseAndFlush();
    }

    private static void ConfigureLogging()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PdfStampNgrokDesktop");
        var logDir = Path.Combine(root, "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 5 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true)
            .CreateLogger();
    }

    private static void RegisterGlobalExceptionHandlers()
    {
        Current.DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled UI exception.");
            MessageBox.Show(
                UiText.Format(
                    "UnhandledErrorMessageTemplate",
                    "Co loi khong mong muon.\nVui long gui file log cho doi phat trien.\n{0}",
                    GetSupportContactText()),
                UiText.Get("UnhandledErrorTitle", "Ung dung gap loi"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "Unhandled domain exception.");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };
    }

    private static bool ResolveDeveloperModeFlag()
    {
        var env = (Environment.GetEnvironmentVariable("PDFSTAMP_DEV_MODE") ?? string.Empty).Trim();
        if (string.Equals(env, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(env, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var appDataFlag = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PdfStampNgrokDesktop",
            ".dev-mode");
        if (File.Exists(appDataFlag))
        {
            return true;
        }

        var appBaseFlag = Path.Combine(AppContext.BaseDirectory, ".dev-mode");
        return File.Exists(appBaseFlag);
    }

    private static string GetSupportContactText()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, SupportContactFileName);
            if (!File.Exists(path))
            {
                return UiText.Format("SupportContactMissingTemplate", "Lien he ho tro: chua cau hinh ({0}).", SupportContactFileName);
            }

            var line = File.ReadLines(path)
                .Select(x => x.Trim())
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith("#", StringComparison.Ordinal));

            if (string.IsNullOrWhiteSpace(line))
            {
                return UiText.Format("SupportContactMissingTemplate", "Lien he ho tro: chua cau hinh ({0}).", SupportContactFileName);
            }

            return UiText.Format("SupportContactTemplate", "Lien he ho tro: {0}", line);
        }
        catch
        {
            return UiText.Format("SupportContactMissingTemplate", "Lien he ho tro: chua cau hinh ({0}).", SupportContactFileName);
        }
    }
}
