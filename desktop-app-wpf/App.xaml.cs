using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
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
                "Ứng dụng không khởi động được.\nXem log tại %APPDATA%\\PdfStampNgrokDesktop\\logs.\n" + GetSupportContactText(),
                "Lỗi khởi động",
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
                "Có lỗi không mong muốn.\nVui lòng gửi file log cho đội phát triển.\n" + GetSupportContactText(),
                "Ứng dụng gặp lỗi",
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

    private static string GetSupportContactText()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, SupportContactFileName);
            if (!File.Exists(path))
            {
                return $"Liên hệ hỗ trợ: chưa cấu hình ({SupportContactFileName}).";
            }

            var line = File.ReadLines(path)
                .Select(x => x.Trim())
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith("#", StringComparison.Ordinal));

            if (string.IsNullOrWhiteSpace(line))
            {
                return $"Liên hệ hỗ trợ: chưa cấu hình ({SupportContactFileName}).";
            }

            return $"Liên hệ hỗ trợ: {line}";
        }
        catch
        {
            return $"Liên hệ hỗ trợ: chưa cấu hình ({SupportContactFileName}).";
        }
    }
}
