using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PdfStampNgrokDesktop.Helpers;
using PdfStampNgrokDesktop.ViewModels;
using Serilog;
using Forms = System.Windows.Forms;

namespace PdfStampNgrokDesktop;

public partial class MainWindow : Window
{
    private const ModifierKeys DeveloperPanelShortcutModifiers =
        ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt;
    private const string DefaultDeveloperPanelPasswordHash = "5ad3e8e883a308649cbbd787a965b7e0a671c1342fdf53ffe725b9f6fb122ac9";
    private const int MaxAttemptsBeforeLock = 3;
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupRegistryName = "PdfStampNgrokDesktop";
    private static readonly TimeSpan DeveloperPanelBaseLockDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeveloperPanelMaxLockDuration = TimeSpan.FromMinutes(15);
    private static readonly string DeveloperPanelPasswordHash = ResolveDeveloperPanelPasswordHash();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly MainViewModel _viewModel;
    private readonly string _developerPanelStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PdfStampNgrokDesktop",
        "dev-panel-guard.json");
    private bool _allowClose;
    private bool _isClosingInProgress;
    private bool _forceCloseAfterShutdown;
    private int _developerPanelFailedAttempts;
    private int _developerPanelLockCount;
    private DateTime _developerPanelLockedUntilUtc = DateTime.MinValue;
    private Icon? _trayApplicationIcon;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _trayCreateLinkMenuItem;
    private Forms.ToolStripMenuItem? _trayCopyLinkMenuItem;
    private Forms.ToolStripMenuItem? _trayCancelLinkMenuItem;
    private Forms.ToolStripMenuItem? _trayExitMenuItem;
    private bool _hasShownTrayHint;
    private enum TaskbarEdge
    {
        Bottom,
        Top,
        Left,
        Right,
    }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeTrayIcon();
        LoadDeveloperPanelGuardState();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        StateChanged += MainWindow_StateChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
        ApplyStartupRegistration();
        RefreshTrayMenuState();

        if (_viewModel.MinimizeToTrayEnabled)
        {
            HideToTray(showHint: true);
        }

        _ = RunStartupAutoCreateLinkAsync();
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_forceCloseAfterShutdown)
        {
            e.Cancel = false;
            return;
        }

        if (_isClosingInProgress)
        {
            e.Cancel = true;
            return;
        }

        if (!_allowClose)
        {
            e.Cancel = true;
            if (_viewModel.MinimizeToTrayEnabled)
            {
                HideToTray(showHint: true);
                return;
            }
        }

        e.Cancel = true;
        _isClosingInProgress = true;
        await _viewModel.ShutdownAsync();
        _forceCloseAfterShutdown = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayMenu?.Dispose();
        _trayMenu = null;

        _trayApplicationIcon?.Dispose();
        _trayApplicationIcon = null;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (!_viewModel.MinimizeToTrayEnabled)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            HideToTray(showHint: false);
        }
    }

    private async Task RunStartupAutoCreateLinkAsync()
    {
        if (!_viewModel.AutoCreateLinkOnStartupEnabled)
        {
            return;
        }

        Log.Information(
            "Startup auto-link preflight: hasProfile={HasProfile}, minimizeToTray={MinimizeToTray}.",
            _viewModel.HasActiveProfile,
            _viewModel.MinimizeToTrayEnabled);

        var result = await _viewModel.TryAutoCreateLinkOnStartupAsync(maxAttempts: 3);
        RefreshTrayMenuState();

        if (result.IsSuccess && !string.IsNullOrWhiteSpace(_viewModel.StampUrl))
        {
            ShowTrayBalloon(
                UiText.Get("TrayAutoLinkSuccessTitle", "Da co link"),
                UiText.Get("TrayAutoLinkSuccessBody", "App da tu tao link sau khi khoi dong."),
                Forms.ToolTipIcon.Info);
            return;
        }

        if (!result.IsSuccess)
        {
            ShowTrayBalloon(
                UiText.Get("TrayAutoLinkFailTitle", "Khong tu tao duoc link"),
                UiText.Get("TrayAutoLinkFailBody", "Bam icon de mo app va tao link thu cong."),
                Forms.ToolTipIcon.Warning);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(MainViewModel.StampUrl), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(MainViewModel.IsBusy), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(MainViewModel.StatusMessage), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(MainViewModel.BadgeText), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(MainViewModel.SelectedProfileId), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(MainViewModel.IsSessionLocked), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(MainViewModel.IsBackendLostForTray), StringComparison.Ordinal))
        {
            RefreshTrayMenuState();
        }
    }

    private void InitializeTrayIcon()
    {
        _trayCreateLinkMenuItem = new Forms.ToolStripMenuItem(UiText.Get("TrayCreateLinkMenu", "Tao link"));
        _trayCopyLinkMenuItem = new Forms.ToolStripMenuItem(UiText.Get("TrayCopyLinkMenu", "Copy link"));
        _trayCancelLinkMenuItem = new Forms.ToolStripMenuItem(UiText.Get("TrayCancelLinkMenu", "Huy link"));
        _trayExitMenuItem = new Forms.ToolStripMenuItem(UiText.Get("TrayExitMenu", "Thoat"));

        _trayCreateLinkMenuItem.Click += async (_, _) => await HandleTrayCreateLinkAsync();
        _trayCopyLinkMenuItem.Click += async (_, _) => await HandleTrayCopyLinkAsync();
        _trayCancelLinkMenuItem.Click += async (_, _) => await HandleTrayCancelLinkAsync();
        _trayExitMenuItem.Click += async (_, _) => await HandleTrayExitAsync();

        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.AddRange(
        [
            _trayCreateLinkMenuItem,
            _trayCopyLinkMenuItem,
            _trayCancelLinkMenuItem,
            new Forms.ToolStripSeparator(),
            _trayExitMenuItem,
        ]);

        _trayApplicationIcon = TryLoadTrayIcon();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayApplicationIcon ?? SystemIcons.Application,
            Visible = true,
            Text = BuildTrayText(),
        };
        _trayIcon.MouseUp += TrayIcon_MouseUp;
    }

    private void TrayIcon_MouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            RestoreFromTray();
            return;
        }

        if (e.Button != Forms.MouseButtons.Right || _trayMenu is null)
        {
            return;
        }

        RefreshTrayMenuState();

        var cursor = Forms.Cursor.Position;
        var menuSize = _trayMenu.PreferredSize;
        var screen = Forms.Screen.FromPoint(cursor);
        var working = screen.WorkingArea;
        var edge = DetectTaskbarEdge(screen);

        var x = cursor.X + 10;
        var y = cursor.Y - menuSize.Height + 8;
        if (edge == TaskbarEdge.Top)
        {
            y = cursor.Y + 8;
        }
        else if (edge == TaskbarEdge.Right)
        {
            x = cursor.X - menuSize.Width - 8;
        }
        else if (edge == TaskbarEdge.Left)
        {
            x = cursor.X + 8;
        }

        if (x + menuSize.Width > working.Right)
        {
            x = working.Right - menuSize.Width;
        }

        if (x < working.Left)
        {
            x = working.Left;
        }

        if (y + menuSize.Height > working.Bottom)
        {
            y = working.Bottom - menuSize.Height;
        }

        if (y < working.Top)
        {
            y = working.Top;
        }

        _trayMenu.Show(x, y);
    }

    private async Task HandleTrayCreateLinkAsync()
    {
        var result = await _viewModel.CreateLinkFromTrayAsync();
        RefreshTrayMenuState();
        ShowTrayBalloon(
            result.IsSuccess
                ? UiText.Get("TrayCreateLinkSuccessTitle", "Da tao link")
                : UiText.Get("TrayCreateLinkFailTitle", "Tao link that bai"),
            result.IsSuccess
                ? UiText.Get("TrayCreateLinkSuccessBody", "Link stamp da san sang.")
                : _viewModel.StatusMessage,
            result.IsSuccess ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
    }

    private async Task HandleTrayCopyLinkAsync()
    {
        var result = await _viewModel.CopyLinkFromTrayAsync();
        RefreshTrayMenuState();
        ShowTrayBalloon(
            result.IsSuccess
                ? UiText.Get("TrayCopyLinkSuccessTitle", "Da copy link")
                : UiText.Get("TrayCopyLinkFailTitle", "Khong copy duoc"),
            result.IsSuccess
                ? UiText.Get("TrayCopyLinkSuccessBody", "Link da duoc copy vao clipboard.")
                : result.Message,
            result.IsSuccess ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
    }

    private async Task HandleTrayCancelLinkAsync()
    {
        var result = await _viewModel.CancelLinkFromTrayAsync();
        RefreshTrayMenuState();
        ShowTrayBalloon(
            result.IsSuccess
                ? UiText.Get("TrayCancelLinkSuccessTitle", "Da huy link")
                : UiText.Get("TrayCancelLinkFailTitle", "Huy link that bai"),
            result.IsSuccess
                ? UiText.Get("TrayCancelLinkSuccessBody", "Link ngrok da duoc huy.")
                : _viewModel.StatusMessage,
            result.IsSuccess ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
    }

    private async Task HandleTrayExitAsync()
    {
        _allowClose = true;
        await Dispatcher.InvokeAsync(Close);
    }

    private void HideToTray(bool showHint)
    {
        ShowInTaskbar = false;
        Hide();
        if (showHint && !_hasShownTrayHint)
        {
            _hasShownTrayHint = true;
            ShowTrayBalloon(
                UiText.Get("TrayRunningTitle", "App dang chay nen"),
                UiText.Get("TrayRunningBody", "Bam icon de mo lai ung dung."),
                Forms.ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        if (IsVisible)
        {
            Activate();
            return;
        }

        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void RefreshTrayMenuState()
    {
        if (_trayIcon is null)
        {
            return;
        }

        if (_trayCreateLinkMenuItem is not null)
        {
            _trayCreateLinkMenuItem.Enabled = _viewModel.CreateLinkCommand.CanExecute(null);
        }

        if (_trayCopyLinkMenuItem is not null)
        {
            _trayCopyLinkMenuItem.Enabled = _viewModel.CopyCommand.CanExecute(null);
        }

        if (_trayCancelLinkMenuItem is not null)
        {
            _trayCancelLinkMenuItem.Enabled = _viewModel.CancelLinkCommand.CanExecute(null);
        }

        _trayIcon.Text = BuildTrayText();
    }

    private string BuildTrayText()
    {
        var title = UiText.Get("TitleWindow", "Sao Y Dien Tu PDF");
        string status;
        if (_viewModel.IsBusy)
        {
            status = UiText.Get("TrayStatusCreating", "Dang tao link");
        }
        else if (_viewModel.IsBackendLostForTray)
        {
            status = UiText.Get("TrayStatusBackendLost", "Mat backend");
        }
        else if (!string.IsNullOrWhiteSpace(_viewModel.StampUrl))
        {
            status = UiText.Get("TrayStatusLinkReady", "Da co link");
        }
        else
        {
            status = UiText.Get("TrayStatusReady", "San sang");
        }

        var text = $"{title} - {status}";
        return text.Length <= 63 ? text : text[..63];
    }

    public void BringToFrontFromSingleInstanceSignal()
    {
        RestoreFromTray();
    }

    private static TaskbarEdge DetectTaskbarEdge(Forms.Screen screen)
    {
        var bounds = screen.Bounds;
        var working = screen.WorkingArea;
        if (working.Top > bounds.Top)
        {
            return TaskbarEdge.Top;
        }

        if (working.Left > bounds.Left)
        {
            return TaskbarEdge.Left;
        }

        if (working.Right < bounds.Right)
        {
            return TaskbarEdge.Right;
        }

        return TaskbarEdge.Bottom;
    }

    private static Icon? TryLoadTrayIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
            if (resource?.Stream is null)
            {
                return null;
            }

            using var stream = resource.Stream;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            return new Icon(copy);
        }
        catch
        {
            return null;
        }
    }

    private void ShowTrayBalloon(string title, string body, Forms.ToolTipIcon icon)
    {
        if (_trayIcon is null)
        {
            return;
        }

        try
        {
            _trayIcon.BalloonTipTitle = title;
            _trayIcon.BalloonTipText = body;
            _trayIcon.BalloonTipIcon = icon;
            _trayIcon.ShowBalloonTip(2200);
        }
        catch
        {
            // Ignore tray balloon failures.
        }
    }

    private void ApplyStartupRegistration()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);
            if (runKey is null)
            {
                return;
            }

            if (_viewModel.StartWithWindowsEnabled)
            {
                runKey.SetValue(StartupRegistryName, $"\"{Environment.ProcessPath}\"");
            }
            else
            {
                runKey.DeleteValue(StartupRegistryName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cannot update startup registration.");
        }
    }

    private void Window_OnUserActivity(object sender, System.Windows.Input.InputEventArgs e)
    {
        _viewModel.NotifyUserActivity();

        if (e is not System.Windows.Input.KeyEventArgs keyEvent)
        {
            return;
        }

        if (keyEvent.IsRepeat)
        {
            return;
        }

        if (keyEvent.Key != Key.D || Keyboard.Modifiers != DeveloperPanelShortcutModifiers)
        {
            return;
        }

        keyEvent.Handled = true;

        if (_viewModel.IsDeveloperGoogleSheetPanelVisible)
        {
            if (_viewModel.ToggleDeveloperPanelCommand.CanExecute(null))
            {
                _viewModel.ToggleDeveloperPanelCommand.Execute(null);
            }

            return;
        }

        var nowUtc = DateTime.UtcNow;
        if (_developerPanelLockedUntilUtc > nowUtc)
        {
            var remaining = _developerPanelLockedUntilUtc - nowUtc;
            var remainingText = remaining.TotalSeconds >= 60
                ? UiText.Format("DevPromptMinutesTemplate", "{0} phut", Math.Ceiling(remaining.TotalMinutes).ToString("0"))
                : UiText.Format("DevPromptSecondsTemplate", "{0} giay", Math.Ceiling(remaining.TotalSeconds).ToString("0"));

            MessageBox.Show(
                UiText.Format("DevPromptLockedMessageTemplate", "Tính năng mở bảng ẩn đang tạm khóa. Vui lòng thử lại sau {0}.", remainingText),
                UiText.Get("DevPromptLockedTitle", "Đang tạm khóa"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirm = new ConfirmDeveloperPanelWindow
        {
            Owner = this,
        };

        if (confirm.ShowDialog() != true)
        {
            return;
        }

        if (!string.Equals(ComputeSha256(confirm.EnteredPassword), DeveloperPanelPasswordHash, StringComparison.OrdinalIgnoreCase))
        {
            _developerPanelFailedAttempts++;
            if (_developerPanelFailedAttempts >= MaxAttemptsBeforeLock)
            {
                _developerPanelFailedAttempts = 0;
                _developerPanelLockCount++;

                var lockSeconds = DeveloperPanelBaseLockDuration.TotalSeconds * Math.Pow(2, _developerPanelLockCount - 1);
                var cappedSeconds = Math.Min(lockSeconds, DeveloperPanelMaxLockDuration.TotalSeconds);
                var lockDuration = TimeSpan.FromSeconds(cappedSeconds);
                _developerPanelLockedUntilUtc = DateTime.UtcNow.Add(lockDuration);

                SaveDeveloperPanelGuardState();

                MessageBox.Show(
                    UiText.Format(
                        "DevPromptLockTriggeredMessageTemplate",
                        "Sai mật mã quá {0} lần. Tạm khóa {1} giây.",
                        MaxAttemptsBeforeLock,
                        Math.Ceiling(lockDuration.TotalSeconds).ToString("0")),
                    UiText.Get("DevPromptLockedTitle", "Đang tạm khóa"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            SaveDeveloperPanelGuardState();

            var remainingAttempts = MaxAttemptsBeforeLock - _developerPanelFailedAttempts;
            MessageBox.Show(
                UiText.Format("DevPromptWrongMessageTemplate", "Sai mật mã. Còn {0} lần thử trước khi bị tạm khóa.", remainingAttempts),
                UiText.Get("DevPromptWrongTitle", "Sai mật mã"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _developerPanelFailedAttempts = 0;
        _developerPanelLockCount = 0;
        _developerPanelLockedUntilUtc = DateTime.MinValue;
        SaveDeveloperPanelGuardState();

        if (_viewModel.ToggleDeveloperPanelCommand.CanExecute(null))
        {
            _viewModel.ToggleDeveloperPanelCommand.Execute(null);
        }
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.NotifyUserActivity();
        RestoreFromTray();
        var guideWindow = new GuideWindow
        {
            Owner = this,
        };
        guideWindow.ShowDialog();
    }

    private void LoadDeveloperPanelGuardState()
    {
        try
        {
            if (!File.Exists(_developerPanelStatePath))
            {
                return;
            }

            var json = File.ReadAllText(_developerPanelStatePath);
            var state = JsonSerializer.Deserialize<DeveloperPanelGuardState>(json);
            if (state is null)
            {
                return;
            }

            _developerPanelFailedAttempts = Math.Max(0, state.FailedAttempts);
            _developerPanelLockCount = Math.Max(0, state.LockCount);
            _developerPanelLockedUntilUtc = state.LockedUntilUtc;
        }
        catch
        {
            _developerPanelFailedAttempts = 0;
            _developerPanelLockCount = 0;
            _developerPanelLockedUntilUtc = DateTime.MinValue;
        }
    }

    private void SaveDeveloperPanelGuardState()
    {
        try
        {
            var dir = Path.GetDirectoryName(_developerPanelStatePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var state = new DeveloperPanelGuardState
            {
                FailedAttempts = _developerPanelFailedAttempts,
                LockCount = _developerPanelLockCount,
                LockedUntilUtc = _developerPanelLockedUntilUtc,
            };

            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(_developerPanelStatePath, json);
        }
        catch
        {
            // Ignore persistence failures for dev guard state.
        }
    }

    private static string ResolveDeveloperPanelPasswordHash()
    {
        var fromHashEnv = (Environment.GetEnvironmentVariable("PDFSTAMP_DEV_PANEL_PASSWORD_HASH") ?? string.Empty).Trim();
        if (IsHexSha256(fromHashEnv))
        {
            return fromHashEnv.ToLowerInvariant();
        }

        var fromPlainEnv = (Environment.GetEnvironmentVariable("PDFSTAMP_DEV_PANEL_PASSWORD") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromPlainEnv))
        {
            return ComputeSha256(fromPlainEnv);
        }

        return DefaultDeveloperPanelPasswordHash;
    }

    private static bool IsHexSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            var isHexDigit = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!isHexDigit)
            {
                return false;
            }
        }

        return true;
    }

    private static string ComputeSha256(string input)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input ?? string.Empty));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class DeveloperPanelGuardState
    {
        public int FailedAttempts { get; set; }

        public int LockCount { get; set; }

        public DateTime LockedUntilUtc { get; set; }
    }
}
