using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using PdfStampNgrokDesktop.Helpers;
using PdfStampNgrokDesktop.ViewModels;

namespace PdfStampNgrokDesktop;

public partial class MainWindow : Window
{
    private const ModifierKeys DeveloperPanelShortcutModifiers =
        ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt;
    private const string DefaultDeveloperPanelPasswordHash = "5ad3e8e883a308649cbbd787a965b7e0a671c1342fdf53ffe725b9f6fb122ac9";
    private const int MaxAttemptsBeforeLock = 3;
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

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        LoadDeveloperPanelGuardState();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
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
            var dialog = new ConfirmCloseWindow
            {
                Owner = this,
            };

            var result = dialog.ShowDialog();
            if (result != true)
            {
                e.Cancel = true;
                return;
            }

            _allowClose = true;
        }

        e.Cancel = true;
        _isClosingInProgress = true;
        await _viewModel.ShutdownAsync();
        _forceCloseAfterShutdown = true;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            Close();
        }));
    }

    private void Window_OnUserActivity(object sender, InputEventArgs e)
    {
        _viewModel.NotifyUserActivity();

        if (e is not KeyEventArgs keyEvent)
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
