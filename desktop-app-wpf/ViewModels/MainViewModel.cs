using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Options;
using PdfStampNgrokDesktop.Core;
using PdfStampNgrokDesktop.Helpers;
using PdfStampNgrokDesktop.Models;
using PdfStampNgrokDesktop.Options;
using PdfStampNgrokDesktop.Services;
using Serilog;

namespace PdfStampNgrokDesktop.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ITokenStoreService _tokenStoreService;
    private readonly IBackendService _backendService;
    private readonly INgrokService _ngrokService;
    private readonly IHealthMonitorService _healthMonitorService;
    private readonly IUpdateService _updateService;
    private readonly AppRuntimeOptions _runtimeOptions;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _copyFeedbackTimer;
    private readonly DispatcherTimer _revealTimer;

    private AppConfig _config = new();
    private bool _initialized;
    private bool _busy;
    private bool _keepTunnelAlive;
    private bool _isRefreshInProgress;
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    private string _selectedProfileId = string.Empty;
    private string _newProfileName = string.Empty;
    private string _newProfileToken = string.Empty;
    private bool _isRevealTokenInput;
    private bool _isSessionLocked;
    private bool _autoCopyOnGenerate;
    private string _stampUrl = string.Empty;
    private string _statusMessage = "Sẵn sàng.";
    private string _realtimeState = "Đã sẵn sàng";
    private string _copyButtonText = "Copy";
    private string _badgeText = "Chưa có link";
    private LinkIndicator _badgeIndicator = LinkIndicator.Idle;
    private UpdateChannel _selectedUpdateChannel = UpdateChannel.Stable;
    private string _updateHint = "Kênh cập nhật: stable";

    public MainViewModel(
        ITokenStoreService tokenStoreService,
        IBackendService backendService,
        INgrokService ngrokService,
        IHealthMonitorService healthMonitorService,
        IUpdateService updateService,
        IOptions<AppRuntimeOptions> runtimeOptions)
    {
        _tokenStoreService = tokenStoreService;
        _backendService = backendService;
        _ngrokService = ngrokService;
        _healthMonitorService = healthMonitorService;
        _updateService = updateService;
        _runtimeOptions = runtimeOptions.Value;

        Profiles = new ObservableCollection<ProfileItemViewModel>();

        AddTokenCommand = new AsyncRelayCommand(AddTokenAsync, CanUseSensitiveAction);
        UseTokenCommand = new AsyncRelayCommand(UseTokenAsync, CanUseTokenAction);
        RemoveTokenCommand = new AsyncRelayCommand(RemoveTokenAsync, CanRemoveTokenAction);
        CreateLinkCommand = new AsyncRelayCommand(CreateLinkAsync, CanCreateLinkAction);
        CancelLinkCommand = new AsyncRelayCommand(CancelLinkAsync, CanCancelLink);
        CopyCommand = new RelayCommand(CopyStampUrl, CanCopy);
        ToggleRevealCommand = new RelayCommand(ToggleRevealTokenInput, () => !_busy);
        UnlockCommand = new RelayCommand(UnlockSession, () => IsSessionLocked);
        RestoreBackupCommand = new AsyncRelayCommand(RestoreBackupAsync, () => !_busy);
        CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync, () => !_busy);

        UpdateChannels = Enum.GetValues<UpdateChannel>();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(2, _runtimeOptions.RefreshIntervalSeconds)),
        };
        _refreshTimer.Tick += RefreshTimer_Tick;

        _copyFeedbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _copyFeedbackTimer.Tick += (_, _) =>
        {
            _copyFeedbackTimer.Stop();
            CopyButtonText = "Copy";
        };

        _revealTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(5, _runtimeOptions.TokenRevealSeconds)),
        };
        _revealTimer.Tick += (_, _) =>
        {
            _revealTimer.Stop();
            IsRevealTokenInput = false;
        };

        _backendService.Exited += (_, code) =>
        {
            Log.Warning("Backend exited with code {Code}.", code);
        };

        _ngrokService.Exited += (_, code) =>
        {
            Log.Warning("ngrok exited with code {Code}.", code);
        };
    }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; }

    public ICommand AddTokenCommand { get; }

    public ICommand UseTokenCommand { get; }

    public ICommand RemoveTokenCommand { get; }

    public ICommand CreateLinkCommand { get; }

    public ICommand CancelLinkCommand { get; }

    public ICommand CopyCommand { get; }

    public ICommand ToggleRevealCommand { get; }

    public ICommand UnlockCommand { get; }

    public ICommand RestoreBackupCommand { get; }

    public ICommand CheckUpdateCommand { get; }

    public IReadOnlyList<UpdateChannel> UpdateChannels { get; }

    public string SelectedProfileId
    {
        get => _selectedProfileId;
        set
        {
            SetProperty(ref _selectedProfileId, value);
            NotifyCommandStates();
        }
    }

    public string NewProfileName
    {
        get => _newProfileName;
        set => SetProperty(ref _newProfileName, value);
    }

    public string NewProfileToken
    {
        get => _newProfileToken;
        set => SetProperty(ref _newProfileToken, value);
    }

    public bool IsRevealTokenInput
    {
        get => _isRevealTokenInput;
        set => SetProperty(ref _isRevealTokenInput, value);
    }

    public bool IsSessionLocked
    {
        get => _isSessionLocked;
        private set
        {
            SetProperty(ref _isSessionLocked, value);
            NotifyCommandStates();
        }
    }

    public bool AutoCopyOnGenerate
    {
        get => _autoCopyOnGenerate;
        set
        {
            SetProperty(ref _autoCopyOnGenerate, value);
            _config.Ui.AutoCopyOnGenerate = value;
            _ = _tokenStoreService.SaveAsync(_config);
        }
    }

    public string StampUrl
    {
        get => _stampUrl;
        private set
        {
            SetProperty(ref _stampUrl, value);
            NotifyCommandStates();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string RealtimeState
    {
        get => _realtimeState;
        private set => SetProperty(ref _realtimeState, value);
    }

    public string CopyButtonText
    {
        get => _copyButtonText;
        private set => SetProperty(ref _copyButtonText, value);
    }

    public string BadgeText
    {
        get => _badgeText;
        private set => SetProperty(ref _badgeText, value);
    }

    public LinkIndicator BadgeIndicator
    {
        get => _badgeIndicator;
        private set => SetProperty(ref _badgeIndicator, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            SetProperty(ref _busy, value);
            NotifyCommandStates();
        }
    }

    public bool CanShowLockBanner => IsSessionLocked;

    public UpdateChannel SelectedUpdateChannel
    {
        get => _selectedUpdateChannel;
        set
        {
            SetProperty(ref _selectedUpdateChannel, value);
            _config.Update.Channel = value;
            UpdateHint = $"Kênh cập nhật: {value.ToString().ToLowerInvariant()}";
            _ = _tokenStoreService.SaveAsync(_config);
        }
    }

    public string UpdateHint
    {
        get => _updateHint;
        private set => SetProperty(ref _updateHint, value);
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        RealtimeState = "Đang kiểm tra";
        StatusMessage = "Đang tải cấu hình...";

        var loadResult = await _tokenStoreService.LoadAsync();
        if (!loadResult.IsSuccess || loadResult.Value is null)
        {
            BadgeText = "Lỗi";
            BadgeIndicator = LinkIndicator.Error;
            RealtimeState = "Lỗi";
            StatusMessage = $"{loadResult.Code}: {loadResult.Message}";
            return;
        }

        _config = loadResult.Value;
        _config.Ui.AutoCopyOnGenerate = false;
        AutoCopyOnGenerate = false;
        SelectedUpdateChannel = _config.Update.Channel;
        await _tokenStoreService.SaveAsync(_config);
        RefreshProfiles();

        _refreshTimer.Start();
        RealtimeState = "Đã sẵn sàng";
        StatusMessage = "Sẵn sàng. Chọn token rồi bấm 'Tạo link'.";
        await RefreshHealthAsync();
        _ = CheckForUpdatesInBackgroundAsync();
        _initialized = true;
    }

    public async Task ShutdownAsync()
    {
        _refreshTimer.Stop();
        _copyFeedbackTimer.Stop();
        _revealTimer.Stop();
        await _ngrokService.StopAsync();
        await _backendService.StopAsync();
    }

    public void NotifyUserActivity()
    {
        _lastActivityUtc = DateTime.UtcNow;
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_isRefreshInProgress)
        {
            return;
        }

        _isRefreshInProgress = true;
        try
        {
            HandleSessionAutoLock();

            if (_keepTunnelAlive && !IsSessionLocked && !_ngrokService.IsRunning && !string.IsNullOrWhiteSpace(GetActiveProfileId()))
            {
                await TrySelfRecoverTunnelAsync();
            }

            await RefreshTunnelUrlFromNgrokAsync();
            await RefreshHealthAsync();
            MonitorProcessMemory();
        }
        finally
        {
            _isRefreshInProgress = false;
        }
    }

    private void HandleSessionAutoLock()
    {
        var minutes = Math.Max(1, _config.Security.AutoLockMinutes);
        if (IsSessionLocked)
        {
            return;
        }

        if (DateTime.UtcNow - _lastActivityUtc < TimeSpan.FromMinutes(minutes))
        {
            return;
        }

        IsSessionLocked = true;
        IsRevealTokenInput = false;
        _revealTimer.Stop();
        RealtimeState = "Đang kiểm tra";
        StatusMessage = "Phiên đã tự khóa do không thao tác.";
        RaisePropertyChanged(nameof(CanShowLockBanner));
    }

    private void UnlockSession()
    {
        IsSessionLocked = false;
        _lastActivityUtc = DateTime.UtcNow;
        RealtimeState = "Đã sẵn sàng";
        StatusMessage = "Đã mở khóa phiên.";
        RaisePropertyChanged(nameof(CanShowLockBanner));
    }

    private async Task AddTokenAsync()
    {
        if (!EnsureUnlocked())
        {
            return;
        }

        var token = (NewProfileToken ?? string.Empty).Trim();
        var name = (NewProfileName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            SetUiError("Token không được để trống.", ErrorCode.InvalidInput);
            return;
        }

        if (await TokenExistsAsync(token))
        {
            SetUiError("Token này đã tồn tại.", ErrorCode.InvalidInput);
            return;
        }

        var encrypted = _tokenStoreService.ProtectToken(token);
        if (!encrypted.IsSuccess || string.IsNullOrWhiteSpace(encrypted.Value))
        {
            SetUiError(encrypted.Message, encrypted.Code);
            return;
        }

        IsBusy = true;
        RealtimeState = "Đang tạo";
        StatusMessage = "Đang thêm token...";

        try
        {
            var profile = new NgrokProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? $"Token {Profiles.Count + 1}" : name,
                EncryptedToken = encrypted.Value,
            };

            _config.Profiles.Add(profile);
            _config.ActiveProfileId = profile.Id;
            var save = await _tokenStoreService.SaveAsync(_config);
            if (!save.IsSuccess)
            {
                SetUiError(save.Message, save.Code);
                return;
            }

            await _ngrokService.StopAsync();
            StampUrl = string.Empty;
            _keepTunnelAlive = false;
            NewProfileName = string.Empty;
            NewProfileToken = string.Empty;
            IsRevealTokenInput = false;
            RefreshProfiles();
            RealtimeState = "Đã sẵn sàng";
            StatusMessage = "Đã thêm token.";
        }
        finally
        {
            IsBusy = false;
            await RefreshHealthAsync();
        }
    }

    private async Task UseTokenAsync()
    {
        if (!EnsureUnlocked())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedProfileId))
        {
            SetUiError("Bạn chưa chọn token.", ErrorCode.InvalidInput);
            return;
        }

        IsBusy = true;
        RealtimeState = "Đang kiểm tra";
        StatusMessage = "Đang áp dụng token...";
        try
        {
            _config.ActiveProfileId = SelectedProfileId;
            var save = await _tokenStoreService.SaveAsync(_config);
            if (!save.IsSuccess)
            {
                SetUiError(save.Message, save.Code);
                return;
            }

            await _ngrokService.StopAsync();
            _keepTunnelAlive = false;
            StampUrl = string.Empty;
            RealtimeState = "Đã sẵn sàng";
            StatusMessage = "Đã áp dụng token.";
        }
        finally
        {
            IsBusy = false;
            await RefreshHealthAsync();
        }
    }

    private async Task RemoveTokenAsync()
    {
        if (!EnsureUnlocked())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedProfileId))
        {
            SetUiError("Bạn chưa chọn token để xóa.", ErrorCode.InvalidInput);
            return;
        }

        var result = MessageBox.Show("Xóa token đã chọn?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        RealtimeState = "Đang kiểm tra";
        StatusMessage = "Đang xóa token...";
        try
        {
            _config.Profiles.RemoveAll(x => x.Id == SelectedProfileId);
            _config.ActiveProfileId = _config.Profiles.FirstOrDefault()?.Id;
            var save = await _tokenStoreService.SaveAsync(_config);
            if (!save.IsSuccess)
            {
                SetUiError(save.Message, save.Code);
                return;
            }

            await _ngrokService.StopAsync();
            _keepTunnelAlive = false;
            StampUrl = string.Empty;
            RefreshProfiles();
            StatusMessage = _config.Profiles.Count == 0
                ? "Đã xóa hết token."
                : "Đã xóa token.";
            RealtimeState = "Đã sẵn sàng";
        }
        finally
        {
            IsBusy = false;
            await RefreshHealthAsync();
        }
    }

    private async Task CreateLinkAsync()
    {
        if (!EnsureUnlocked())
        {
            return;
        }

        var tokenResult = GetActivePlainToken();
        if (!tokenResult.IsSuccess || string.IsNullOrWhiteSpace(tokenResult.Value))
        {
            SetUiError(tokenResult.Message, tokenResult.Code);
            return;
        }

        IsBusy = true;
        RealtimeState = "Đang tạo";
        StatusMessage = "Đang khởi động backend và ngrok...";

        try
        {
            var backend = await _backendService.EnsureStartedAsync(_config.Backend.Port);
            if (!backend.IsSuccess)
            {
                SetUiError(backend.Message, backend.Code);
                return;
            }

            var startNgrok = await _ngrokService.StartAsync(_config.Backend.Port, tokenResult.Value, _config.Ngrok.Region, restart: false);
            if (!startNgrok.IsSuccess)
            {
                SetUiError(startNgrok.Message, startNgrok.Code);
                return;
            }

            var tunnel = await WaitForTunnelAsync(TimeSpan.FromSeconds(15));
            if (tunnel is null)
            {
                SetUiError("Không lấy được tunnel ngrok.", ErrorCode.NgrokTunnelUnavailable);
                return;
            }

            StampUrl = tunnel.StampUrl;
            _keepTunnelAlive = true;
            await RefreshHealthAsync();

            StatusMessage = "Đã tạo link ngrok.";
            RealtimeState = "Đã sẵn sàng";
        }
        finally
        {
            IsBusy = false;
            NotifyUserActivity();
        }
    }

    private async Task CancelLinkAsync()
    {
        IsBusy = true;
        RealtimeState = "Đang kiểm tra";
        StatusMessage = "Đang hủy link...";
        try
        {
            await _ngrokService.StopAsync();
            _keepTunnelAlive = false;
            StampUrl = string.Empty;
            RealtimeState = "Đã sẵn sàng";
            StatusMessage = "Đã hủy link.";
        }
        finally
        {
            IsBusy = false;
            await RefreshHealthAsync();
        }
    }

    private void CopyStampUrl()
    {
        var value = (StampUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        Clipboard.SetText(value);
        CopyButtonText = "Đã copy";
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
        StatusMessage = "Đã copy link.";
        RealtimeState = "Đã sẵn sàng";
        NotifyUserActivity();
    }

    private void ToggleRevealTokenInput()
    {
        IsRevealTokenInput = !IsRevealTokenInput;
        _revealTimer.Stop();
        if (IsRevealTokenInput)
        {
            _revealTimer.Start();
        }
    }

    private async Task RestoreBackupAsync()
    {
        IsBusy = true;
        RealtimeState = "Đang kiểm tra";
        StatusMessage = "Đang khôi phục backup cấu hình...";
        try
        {
            var restore = await _tokenStoreService.RestoreLatestBackupAsync();
            if (!restore.IsSuccess || restore.Value is null)
            {
                SetUiError(restore.Message, restore.Code);
                return;
            }

            _config = restore.Value;
            RefreshProfiles();
            await _ngrokService.StopAsync();
            StampUrl = string.Empty;
            _keepTunnelAlive = false;
            StatusMessage = "Đã khôi phục backup gần nhất.";
            RealtimeState = "Đã sẵn sàng";
        }
        finally
        {
            IsBusy = false;
            await RefreshHealthAsync();
        }
    }

    private async Task CheckUpdateAsync()
    {
        IsBusy = true;
        RealtimeState = "Đang kiểm tra";
        StatusMessage = "Đang kiểm tra cập nhật...";
        try
        {
            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            var result = await _updateService.CheckForUpdatesAsync(_config.Update, currentVersion);
            if (!result.IsSuccess)
            {
                SetUiError(result.Message, result.Code);
                return;
            }

            if (result.Value is null)
            {
                RealtimeState = "Đã sẵn sàng";
                StatusMessage = "Không có bản cập nhật mới.";
                return;
            }

            RealtimeState = "Đã sẵn sàng";
            StatusMessage = $"Có bản {result.Value.Version}. Tải: {result.Value.DownloadUrl}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshTunnelUrlFromNgrokAsync()
    {
        if (!_ngrokService.IsRunning)
        {
            return;
        }

        var tunnelResult = await _ngrokService.GetCurrentTunnelAsync();
        if (!tunnelResult.IsSuccess)
        {
            return;
        }

        var tunnel = tunnelResult.Value;
        if (tunnel is null)
        {
            return;
        }

        if (!string.Equals(StampUrl, tunnel.StampUrl, StringComparison.Ordinal))
        {
            StampUrl = tunnel.StampUrl;
        }
    }

    private async Task RefreshHealthAsync()
    {
        var result = await _healthMonitorService.CheckAsync(_ngrokService.IsRunning, StampUrl, _ngrokService.LastError);
        if (!result.IsSuccess || result.Value is null)
        {
            BadgeIndicator = LinkIndicator.Error;
            BadgeText = "Lỗi";
            return;
        }

        BadgeIndicator = result.Value.Indicator;
        BadgeText = result.Value.BadgeText;

        if (!IsBusy && !IsSessionLocked)
        {
            RealtimeState = result.Value.Indicator switch
            {
                LinkIndicator.Healthy => "Đã sẵn sàng",
                LinkIndicator.Degraded => "Đang kiểm tra",
                LinkIndicator.Error => "Lỗi",
                _ => "Đã sẵn sàng",
            };
        }
    }

    private async Task TrySelfRecoverTunnelAsync()
    {
        var tokenResult = GetActivePlainToken();
        if (!tokenResult.IsSuccess || string.IsNullOrWhiteSpace(tokenResult.Value))
        {
            return;
        }

        Log.Warning("Watchdog: ngrok stopped unexpectedly, recovering tunnel.");
        var backend = await _backendService.EnsureStartedAsync(_config.Backend.Port);
        if (!backend.IsSuccess)
        {
            return;
        }

        var ngrok = await _ngrokService.StartAsync(_config.Backend.Port, tokenResult.Value, _config.Ngrok.Region, restart: true);
        if (!ngrok.IsSuccess)
        {
            return;
        }

        var tunnel = await WaitForTunnelAsync(TimeSpan.FromSeconds(10));
        if (tunnel is null)
        {
            return;
        }

        StampUrl = tunnel.StampUrl;
        StatusMessage = "Watchdog đã khôi phục tunnel.";
    }

    private async Task<TunnelInfo?> WaitForTunnelAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = await _ngrokService.GetCurrentTunnelAsync();
            if (result.IsSuccess && result.Value is not null)
            {
                return result.Value;
            }

            await Task.Delay(350);
        }

        return null;
    }

    private async Task<bool> TokenExistsAsync(string token)
    {
        foreach (var profile in _config.Profiles)
        {
            var decrypted = _tokenStoreService.UnprotectToken(profile.EncryptedToken);
            if (!decrypted.IsSuccess || decrypted.Value is null)
            {
                continue;
            }

            if (string.Equals(decrypted.Value.Trim(), token.Trim(), StringComparison.Ordinal))
            {
                return true;
            }
        }

        await Task.Yield();
        return false;
    }

    private Result<string> GetActivePlainToken()
    {
        var activeId = GetActiveProfileId();
        if (string.IsNullOrWhiteSpace(activeId))
        {
            return Result<string>.Fail(ErrorCode.InvalidInput, "Bạn cần thêm token trước khi tạo link.");
        }

        var profile = _config.Profiles.FirstOrDefault(x => x.Id == activeId);
        if (profile is null)
        {
            return Result<string>.Fail(ErrorCode.NotFound, "Không tìm thấy profile token đang dùng.");
        }

        var tokenResult = _tokenStoreService.UnprotectToken(profile.EncryptedToken);
        if (!tokenResult.IsSuccess || string.IsNullOrWhiteSpace(tokenResult.Value))
        {
            return Result<string>.Fail(tokenResult.Code, tokenResult.Message);
        }

        return Result<string>.Ok(tokenResult.Value.Trim());
    }

    private string GetActiveProfileId()
    {
        if (!string.IsNullOrWhiteSpace(_config.ActiveProfileId))
        {
            return _config.ActiveProfileId;
        }

        return _config.Profiles.FirstOrDefault()?.Id ?? string.Empty;
    }

    private void RefreshProfiles()
    {
        var active = GetActiveProfileId();
        var items = _config.Profiles.Select((profile, index) =>
        {
            var name = string.IsNullOrWhiteSpace(profile.Name) ? $"Token {index + 1}" : profile.Name.Trim();
            var decrypted = _tokenStoreService.UnprotectToken(profile.EncryptedToken);
            var masked = decrypted.IsSuccess ? SensitiveDataMasker.MaskToken(decrypted.Value) : "----";
            return new ProfileItemViewModel
            {
                Id = profile.Id,
                Name = name,
                MaskedToken = masked,
            };
        }).ToList();

        Profiles.Clear();
        foreach (var item in items)
        {
            Profiles.Add(item);
        }

        SelectedProfileId = items.FirstOrDefault(x => x.Id == active)?.Id ?? items.FirstOrDefault()?.Id ?? string.Empty;
        _config.ActiveProfileId = SelectedProfileId;
    }

    private bool EnsureUnlocked()
    {
        if (!IsSessionLocked)
        {
            return true;
        }

        SetUiError("Phiên đang khóa. Bấm 'Mở khóa phiên' trước.", ErrorCode.Unauthorized);
        return false;
    }

    private void SetUiError(string message, ErrorCode code)
    {
        RealtimeState = "Lỗi";
        StatusMessage = $"{code}: {message}";
        BadgeIndicator = LinkIndicator.Error;
        BadgeText = "Lỗi";
    }

    private bool CanUseSensitiveAction()
    {
        return !_busy && !IsSessionLocked;
    }

    private bool HasSelectedProfile()
    {
        return !string.IsNullOrWhiteSpace(SelectedProfileId) && Profiles.Count > 0;
    }

    private bool CanUseTokenAction()
    {
        return CanUseSensitiveAction() && HasSelectedProfile();
    }

    private bool CanRemoveTokenAction()
    {
        return CanUseSensitiveAction() && HasSelectedProfile();
    }

    private bool CanCreateLinkAction()
    {
        return CanUseSensitiveAction() && HasSelectedProfile();
    }

    private bool CanCancelLink()
    {
        return !_busy && !IsSessionLocked && _ngrokService.IsRunning;
    }

    private bool CanCopy()
    {
        return !_busy && !string.IsNullOrWhiteSpace(StampUrl);
    }

    private void NotifyCommandStates()
    {
        if (AddTokenCommand is AsyncRelayCommand add)
        {
            add.NotifyCanExecuteChanged();
        }

        if (UseTokenCommand is AsyncRelayCommand use)
        {
            use.NotifyCanExecuteChanged();
        }

        if (RemoveTokenCommand is AsyncRelayCommand remove)
        {
            remove.NotifyCanExecuteChanged();
        }

        if (CreateLinkCommand is AsyncRelayCommand create)
        {
            create.NotifyCanExecuteChanged();
        }

        if (CancelLinkCommand is AsyncRelayCommand cancel)
        {
            cancel.NotifyCanExecuteChanged();
        }

        if (CopyCommand is RelayCommand copy)
        {
            copy.NotifyCanExecuteChanged();
        }

        if (ToggleRevealCommand is RelayCommand toggleReveal)
        {
            toggleReveal.NotifyCanExecuteChanged();
        }

        if (UnlockCommand is RelayCommand unlock)
        {
            unlock.NotifyCanExecuteChanged();
        }

        if (RestoreBackupCommand is AsyncRelayCommand restore)
        {
            restore.NotifyCanExecuteChanged();
        }

        if (CheckUpdateCommand is AsyncRelayCommand checkUpdate)
        {
            checkUpdate.NotifyCanExecuteChanged();
        }
    }

    private async Task CheckForUpdatesInBackgroundAsync()
    {
        try
        {
            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            var result = await _updateService.CheckForUpdatesAsync(_config.Update, currentVersion);
            if (!result.IsSuccess)
            {
                Log.Warning("Background auto-update failed: {Code} - {Message}", result.Code, result.Message);
                return;
            }

            if (result.Value is null)
            {
                return;
            }

            StatusMessage = $"Đang áp dụng cập nhật phiên bản {result.Value.Version}. Ứng dụng sẽ tự mở lại.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unexpected background auto-update failure.");
        }
    }

    private void MonitorProcessMemory()
    {
        var threshold = Math.Max(256, _runtimeOptions.MemoryWarningMb);
        var memoryMb = Process.GetCurrentProcess().PrivateMemorySize64 / (1024 * 1024);
        if (memoryMb <= threshold)
        {
            return;
        }

        Log.Warning("Memory usage high: {MemoryMb} MB", memoryMb);
        if (!IsBusy)
        {
            StatusMessage = $"Cảnh báo: RAM app đang cao ({memoryMb}MB).";
        }
    }
}


