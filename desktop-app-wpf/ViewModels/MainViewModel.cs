using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
    private string _googleSheetId = string.Empty;
    private string _googleSheetTargetCell = "CONFIG!B32";
    private string _googleSheetWebhookUrl = string.Empty;
    private bool _isRevealTokenInput;
    private bool _isSessionLocked;
    private bool _autoCopyOnGenerate;
    private string _stampUrl = string.Empty;
    private string _statusMessage = "Sáºµn sÃ ng.";
    private string _realtimeState = "ÄÃ£ sáºµn sÃ ng";
    private string _copyButtonText = "Copy";
    private string _badgeText = "ChÆ°a cÃ³ link";
    private LinkIndicator _badgeIndicator = LinkIndicator.Idle;
    private UpdateChannel _selectedUpdateChannel = UpdateChannel.Stable;
    private string _updateHint = "KÃªnh cáº­p nháº­t: stable";
    private bool _isDeveloperGoogleSheetPanelVisible;

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
        _isDeveloperGoogleSheetPanelVisible = _runtimeOptions.ShowDeveloperGoogleSheetPanel;

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
        ToggleDeveloperPanelCommand = new RelayCommand(ToggleDeveloperPanel);

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

    public ICommand ToggleDeveloperPanelCommand { get; }

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

    public string GoogleSheetId
    {
        get => _googleSheetId;
        set => SetProperty(ref _googleSheetId, value);
    }

    public string GoogleSheetTargetCell
    {
        get => _googleSheetTargetCell;
        set => SetProperty(ref _googleSheetTargetCell, value);
    }

    public string GoogleSheetWebhookUrl
    {
        get => _googleSheetWebhookUrl;
        set => SetProperty(ref _googleSheetWebhookUrl, value);
    }

    public bool IsDeveloperGoogleSheetPanelVisible
    {
        get => _isDeveloperGoogleSheetPanelVisible;
        private set => SetProperty(ref _isDeveloperGoogleSheetPanelVisible, value);
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
            UpdateHint = $"KÃªnh cáº­p nháº­t: {value.ToString().ToLowerInvariant()}";
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

        RealtimeState = "Äang kiá»ƒm tra";
        StatusMessage = "Äang táº£i cáº¥u hÃ¬nh...";

        var loadResult = await _tokenStoreService.LoadAsync();
        if (!loadResult.IsSuccess || loadResult.Value is null)
        {
            BadgeText = "Lá»—i";
            BadgeIndicator = LinkIndicator.Error;
            RealtimeState = "Lá»—i";
            StatusMessage = $"{loadResult.Code}: {loadResult.Message}";
            return;
        }

        _config = loadResult.Value;
        _config.Ui.AutoCopyOnGenerate = false;
        AutoCopyOnGenerate = false;
        GoogleSheetId = (_config.GoogleSheet?.SheetId ?? string.Empty).Trim();
        GoogleSheetTargetCell = NormalizeGoogleSheetTargetCell(_config.GoogleSheet?.TargetCellA1);
        GoogleSheetWebhookUrl = NormalizeGoogleSheetWebhookUrl(_config.GoogleSheet?.WebhookUrl);
        SelectedUpdateChannel = _config.Update.Channel;
        await _tokenStoreService.SaveAsync(_config);
        RefreshProfiles();

        var preflight = ValidateRuntimeDependencies();
        if (!preflight.IsSuccess)
        {
            SetUiError(preflight.Message, preflight.Code);
            return;
        }

        _refreshTimer.Start();
        RealtimeState = "ÄÃ£ sáºµn sÃ ng";
        StatusMessage = "Sáºµn sÃ ng. Chá»n token rá»“i báº¥m 'Táº¡o link'.";
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
        RealtimeState = "Äang kiá»ƒm tra";
        StatusMessage = "PhiÃªn Ä‘Ã£ tá»± khÃ³a do khÃ´ng thao tÃ¡c.";
        RaisePropertyChanged(nameof(CanShowLockBanner));
    }

    private void UnlockSession()
    {
        IsSessionLocked = false;
        _lastActivityUtc = DateTime.UtcNow;
        RealtimeState = "ÄÃ£ sáºµn sÃ ng";
        StatusMessage = "ÄÃ£ má»Ÿ khÃ³a phiÃªn.";
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
            SetUiError("Token khÃ´ng Ä‘Æ°á»£c Ä‘á»ƒ trá»‘ng.", ErrorCode.InvalidInput);
            return;
        }

        if (await TokenExistsAsync(token))
        {
            SetUiError("Token nÃ y Ä‘Ã£ tá»“n táº¡i.", ErrorCode.InvalidInput);
            return;
        }

        var encrypted = _tokenStoreService.ProtectToken(token);
        if (!encrypted.IsSuccess || string.IsNullOrWhiteSpace(encrypted.Value))
        {
            SetUiError(encrypted.Message, encrypted.Code);
            return;
        }

        IsBusy = true;
        RealtimeState = "Äang táº¡o";
        StatusMessage = "Äang thÃªm token...";

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
            RealtimeState = "ÄÃ£ sáºµn sÃ ng";
            StatusMessage = "ÄÃ£ thÃªm token.";
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
            SetUiError("Báº¡n chÆ°a chá»n token.", ErrorCode.InvalidInput);
            return;
        }

        IsBusy = true;
        RealtimeState = "Äang kiá»ƒm tra";
        StatusMessage = "Äang Ã¡p dá»¥ng token...";
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
            RealtimeState = "ÄÃ£ sáºµn sÃ ng";
            StatusMessage = "ÄÃ£ Ã¡p dá»¥ng token.";
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
            SetUiError("Báº¡n chÆ°a chá»n token Ä‘á»ƒ xÃ³a.", ErrorCode.InvalidInput);
            return;
        }

        var confirmDialog = new ConfirmDeleteTokenWindow
        {
            Owner = Application.Current?.MainWindow,
        };
        var confirmed = confirmDialog.ShowDialog() == true;
        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        RealtimeState = "Äang kiá»ƒm tra";
        StatusMessage = "Äang xÃ³a token...";
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
                ? "ÄÃ£ xÃ³a háº¿t token."
                : "ÄÃ£ xÃ³a token.";
            RealtimeState = "ÄÃ£ sáºµn sÃ ng";
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
        RealtimeState = "Äang táº¡o";
        StatusMessage = "Äang khá»Ÿi Ä‘á»™ng backend vÃ  ngrok...";

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
                _keepTunnelAlive = false;
                SetUiError(startNgrok.Message, startNgrok.Code);
                return;
            }

            var tunnel = await WaitForTunnelAsync(TimeSpan.FromSeconds(15));
            if (tunnel is null)
            {
                _keepTunnelAlive = false;
                SetUiError("KhÃ´ng láº¥y Ä‘Æ°á»£c tunnel ngrok.", ErrorCode.NgrokTunnelUnavailable);
                return;
            }

            StampUrl = tunnel.StampUrl;
            _keepTunnelAlive = true;
            await RefreshHealthAsync();
            var syncResult = await PersistAndSyncGoogleSheetAsync(StampUrl);
            if (!syncResult.IsSuccess)
            {
                Log.Warning(
                    "Hidden sheet sync failure: {Code} - {Message}",
                    syncResult.Code,
                    syncResult.Message);
            }

            StatusMessage = "ÄÃ£ táº¡o link.";
            RealtimeState = "ÄÃ£ sáºµn sÃ ng";
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
        RealtimeState = "Äang kiá»ƒm tra";
        StatusMessage = "Äang há»§y link...";
        try
        {
            await _ngrokService.StopAsync();
            _keepTunnelAlive = false;
            StampUrl = string.Empty;
            RealtimeState = "ÄÃ£ sáºµn sÃ ng";
            StatusMessage = "ÄÃ£ há»§y link.";
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
        CopyButtonText = "ÄÃ£ copy";
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
        StatusMessage = "ÄÃ£ copy link.";
        RealtimeState = "ÄÃ£ sáºµn sÃ ng";
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

    private void ToggleDeveloperPanel()
    {
        IsDeveloperGoogleSheetPanelVisible = !IsDeveloperGoogleSheetPanelVisible;
        Log.Information("Developer Google Sheet panel visibility: {Visible}", IsDeveloperGoogleSheetPanelVisible);
    }

    private async Task RestoreBackupAsync()
    {
        IsBusy = true;
        RealtimeState = "Äang kiá»ƒm tra";
        StatusMessage = "Äang khÃ´i phá»¥c backup cáº¥u hÃ¬nh...";
        try
        {
            var restore = await _tokenStoreService.RestoreLatestBackupAsync();
            if (!restore.IsSuccess || restore.Value is null)
            {
                SetUiError(restore.Message, restore.Code);
                return;
            }

            _config = restore.Value;
            GoogleSheetId = (_config.GoogleSheet?.SheetId ?? string.Empty).Trim();
            GoogleSheetTargetCell = NormalizeGoogleSheetTargetCell(_config.GoogleSheet?.TargetCellA1);
            GoogleSheetWebhookUrl = NormalizeGoogleSheetWebhookUrl(_config.GoogleSheet?.WebhookUrl);
            RefreshProfiles();
            await _ngrokService.StopAsync();
            StampUrl = string.Empty;
            _keepTunnelAlive = false;
            StatusMessage = "ÄÃ£ khÃ´i phá»¥c backup gáº§n nháº¥t.";
            RealtimeState = "ÄÃ£ sáºµn sÃ ng";
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
        RealtimeState = "Dang kiem tra";
        StatusMessage = "Dang kiem tra cap nhat...";
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
                RealtimeState = "Da san sang";
                StatusMessage = "Khong co ban cap nhat moi.";
                return;
            }
            await PromptAndApplyUpdateAsync(result.Value, currentVersion, fromBackgroundCheck: false);
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
            BadgeText = "Lá»—i";
            return;
        }

        BadgeIndicator = result.Value.Indicator;
        BadgeText = result.Value.BadgeText;

        if (!IsBusy && !IsSessionLocked)
        {
            RealtimeState = result.Value.Indicator switch
            {
                LinkIndicator.Healthy => "ÄÃ£ sáºµn sÃ ng",
                LinkIndicator.Degraded => "Äang kiá»ƒm tra",
                LinkIndicator.Error => "Lá»—i",
                _ => "ÄÃ£ sáºµn sÃ ng",
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
            if (IsNgrokSessionLimitError(ngrok.Message))
            {
                _keepTunnelAlive = false;
                SetUiError(ngrok.Message, ngrok.Code);
            }
            return;
        }

        var tunnel = await WaitForTunnelAsync(TimeSpan.FromSeconds(10));
        if (tunnel is null)
        {
            return;
        }

        StampUrl = tunnel.StampUrl;
        StatusMessage = "Watchdog Ä‘Ã£ khÃ´i phá»¥c tunnel.";
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

    private async Task<Result> PersistAndSyncGoogleSheetAsync(string endpointUrl)
    {
        var normalizedSheetId = NormalizeGoogleSheetId(GoogleSheetId);
        var normalizedTargetCell = NormalizeGoogleSheetTargetCell(GoogleSheetTargetCell);
        var normalizedWebhookUrl = NormalizeGoogleSheetWebhookUrl(GoogleSheetWebhookUrl);

        GoogleSheetId = normalizedSheetId;
        GoogleSheetTargetCell = normalizedTargetCell;
        GoogleSheetWebhookUrl = normalizedWebhookUrl;

        _config.GoogleSheet ??= new GoogleSheetConfig();
        _config.GoogleSheet.SheetId = normalizedSheetId;
        _config.GoogleSheet.TargetCellA1 = normalizedTargetCell;
        _config.GoogleSheet.WebhookUrl = normalizedWebhookUrl;

        var save = await _tokenStoreService.SaveAsync(_config);
        if (!save.IsSuccess)
        {
            return Result.Fail(save.Code, $"LÆ°u cáº¥u hÃ¬nh Google Sheet tháº¥t báº¡i: {save.Message}");
        }

        if (string.IsNullOrWhiteSpace(normalizedSheetId))
        {
            return Result.Ok("ÄÃ£ táº¡o link ngrok. ChÆ°a nháº­p Google Sheet ID nÃªn bá» qua cáº­p nháº­t tá»± Ä‘á»™ng.");
        }

        var syncResult = await _backendService.SyncGoogleSheetEndpointAsync(
            _config.Backend.Port,
            normalizedSheetId,
            normalizedTargetCell,
            normalizedWebhookUrl,
            endpointUrl);
        if (!syncResult.IsSuccess)
        {
            return Result.Fail(syncResult.Code, $"Cáº­p nháº­t Google Sheet tháº¥t báº¡i: {syncResult.Message}");
        }

        return Result.Ok($"ÄÃ£ táº¡o link vÃ  cáº­p nháº­t Google Sheet ({normalizedTargetCell}).");
    }

    private static string NormalizeGoogleSheetId(string? input)
    {
        var trimmed = (input ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        const string marker = "/spreadsheets/d/";
        var markerIndex = trimmed.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return trimmed;
        }

        var start = markerIndex + marker.Length;
        if (start >= trimmed.Length)
        {
            return trimmed;
        }

        var end = trimmed.IndexOf('/', start);
        if (end < 0)
        {
            end = trimmed.Length;
        }

        var id = trimmed[start..end].Trim();
        return string.IsNullOrWhiteSpace(id) ? trimmed : id;
    }

    private static string NormalizeGoogleSheetTargetCell(string? input)
    {
        var trimmed = (input ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "CONFIG!B32";
        }

        if (!trimmed.Contains('!'))
        {
            return $"CONFIG!{trimmed}";
        }

        return trimmed;
    }

    private static string NormalizeGoogleSheetWebhookUrl(string? input)
    {
        return (input ?? string.Empty).Trim();
    }

    private static bool IsNgrokSessionLimitError(string? message)
    {
        var text = (message ?? string.Empty);
        return text.IndexOf("ERR_NGROK_108", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("simultaneous ngrok agent sessions", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("already online", StringComparison.OrdinalIgnoreCase) >= 0;
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
            return Result<string>.Fail(ErrorCode.InvalidInput, "Báº¡n cáº§n thÃªm token trÆ°á»›c khi táº¡o link.");
        }

        var profile = _config.Profiles.FirstOrDefault(x => x.Id == activeId);
        if (profile is null)
        {
            return Result<string>.Fail(ErrorCode.NotFound, "KhÃ´ng tÃ¬m tháº¥y profile token Ä‘ang dÃ¹ng.");
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

        SetUiError("PhiÃªn Ä‘ang khÃ³a. Báº¥m 'Má»Ÿ khÃ³a phiÃªn' trÆ°á»›c.", ErrorCode.Unauthorized);
        return false;
    }

    private void SetUiError(string message, ErrorCode code)
    {
        RealtimeState = "Lá»—i";
        StatusMessage = $"{code}: {message}";
        BadgeIndicator = LinkIndicator.Error;
        BadgeText = "Lá»—i";
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
            await PromptAndApplyUpdateAsync(result.Value, currentVersion, fromBackgroundCheck: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unexpected background auto-update failure.");
        }
    }
    private async Task PromptAndApplyUpdateAsync(UpdateManifest manifest, string currentVersion, bool fromBackgroundCheck)
    {
        RealtimeState = "Da san sang";
        StatusMessage = $"Co ban cap nhat moi: {manifest.Version}.";
        var dialog = new PdfStampNgrokDesktop.ConfirmUpdateWindow(manifest.Version, fromBackgroundCheck);
        if (Application.Current?.MainWindow is not null)
        {
            dialog.Owner = Application.Current.MainWindow;
        }
        var approve = dialog.ShowDialog() == true;
        if (!approve)
        {
            StatusMessage = "Da bo qua cap nhat. Ban co the cap nhat sau.";
            return;
        }
        RealtimeState = "Dang kiem tra";
        StatusMessage = $"Dang tai ban {manifest.Version}...";
        var applyResult = await _updateService.ApplyUpdateAsync(_config.Update, currentVersion);
        if (!applyResult.IsSuccess)
        {
            SetUiError(applyResult.Message, applyResult.Code);
            return;
        }
        if (applyResult.Value is null)
        {
            RealtimeState = "Da san sang";
            StatusMessage = "Khong tim thay ban cap nhat moi de ap dung.";
            return;
        }
        RealtimeState = "Da san sang";
        StatusMessage = $"Dang ap dung cap nhat {applyResult.Value.Version}. Ung dung se tu mo lai.";
    }

    private Result ValidateRuntimeDependencies()
    {
        // Skip filesystem preflight for test doubles.
        if (_backendService is not BackendService || _ngrokService is not NgrokService)
        {
            return Result.Ok();
        }

        string backendRoot;
        try
        {
            backendRoot = PathResolver.ResolveRepoRoot();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCode.NotFound, $"Thieu backend runtime: {ex.Message}");
        }

        var issues = PathResolver.CollectRuntimeIssues(backendRoot);

        if (issues.Count == 0)
        {
            return Result.Ok();
        }

        return Result.Fail(
            ErrorCode.NotFound,
            $"Runtime chua day du ({string.Join(", ", issues)}). Vui long cap nhat app ban moi nhat.");
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
            StatusMessage = $"Cáº£nh bÃ¡o: RAM app Ä‘ang cao ({memoryMb}MB).";
        }
    }
}



