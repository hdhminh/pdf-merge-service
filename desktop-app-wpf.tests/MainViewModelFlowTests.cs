using Microsoft.Extensions.Options;
using PdfStampNgrokDesktop.Core;
using PdfStampNgrokDesktop.Models;
using PdfStampNgrokDesktop.Options;
using PdfStampNgrokDesktop.Services;
using PdfStampNgrokDesktop.ViewModels;

namespace PdfStampNgrokDesktop.Tests;

public sealed class MainViewModelFlowTests
{
    [StaFact]
    public async Task AddToken_CreateLink_CancelLink_FlowWorks()
    {
        var tokenStore = new InMemoryTokenStoreService();
        var backend = new FakeBackendService();
        var ngrok = new FakeNgrokService();
        var health = new FakeHealthMonitorService();
        var update = new FakeUpdateService();

        var vm = new MainViewModel(
            tokenStore,
            backend,
            ngrok,
            health,
            update,
            Microsoft.Extensions.Options.Options.Create(new AppRuntimeOptions
            {
                RefreshIntervalSeconds = 60,
                TokenRevealSeconds = 10,
            }));

        await vm.InitializeAsync();

        vm.NewProfileName = "Admin";
        vm.NewProfileToken = "2onW-test-token";
        vm.AddTokenCommand.Execute(null);
        await Task.Delay(200);
        Assert.Single(vm.Profiles);

        vm.CreateLinkCommand.Execute(null);
        await Task.Delay(700);
        Assert.Contains("/api/pdf/stamp", vm.StampUrl);
        Assert.Equal("Đã có link", vm.BadgeText);

        vm.CancelLinkCommand.Execute(null);
        await Task.Delay(200);
        Assert.Equal(string.Empty, vm.StampUrl);

        await vm.ShutdownAsync();
    }

    private sealed class InMemoryTokenStoreService : ITokenStoreService
    {
        private AppConfig _config = new();

        public string ConfigPath => "in-memory";

        public string BackupDirectoryPath => "in-memory-backup";

        public Task<Result<AppConfig>> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<AppConfig>.Ok(_config));
        }

        public Task<Result> SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
        {
            _config = config;
            return Task.FromResult(Result.Ok());
        }

        public Task<Result<AppConfig>> RestoreLatestBackupAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<AppConfig>.Ok(_config));
        }

        public Result<string> ProtectToken(string plainToken)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(plainToken);
            return Result<string>.Ok(Convert.ToBase64String(bytes));
        }

        public Result<string> UnprotectToken(string encryptedToken)
        {
            var raw = Convert.FromBase64String(encryptedToken);
            return Result<string>.Ok(System.Text.Encoding.UTF8.GetString(raw));
        }
    }

    private sealed class FakeBackendService : IBackendService
    {
        public bool IsRunning { get; private set; }

        public event EventHandler<int>? Exited;

        public Task<Result> EnsureStartedAsync(int port, CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.FromResult(Result.Ok());
        }

        public Task<Result> StopAsync()
        {
            IsRunning = false;
            return Task.FromResult(Result.Ok());
        }

        public Task<bool> IsHealthyAsync(int port, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(IsRunning);
        }
    }

    private sealed class FakeNgrokService : INgrokService
    {
        public bool IsRunning { get; private set; }

        public string? LastError => null;

        public event EventHandler<int>? Exited;

        public Task<Result> StartAsync(int backendPort, string token, string region, bool restart, CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.FromResult(Result.Ok());
        }

        public Task<Result> StopAsync()
        {
            IsRunning = false;
            return Task.FromResult(Result.Ok());
        }

        public Task<Result<TunnelInfo?>> GetCurrentTunnelAsync(CancellationToken cancellationToken = default)
        {
            if (!IsRunning)
            {
                return Task.FromResult(Result<TunnelInfo?>.Ok(null));
            }

            return Task.FromResult(Result<TunnelInfo?>.Ok(new TunnelInfo
            {
                PublicUrl = "https://fake.ngrok-free.app",
                StampUrl = "https://fake.ngrok-free.app/api/pdf/stamp",
                HealthUrl = "https://fake.ngrok-free.app/health",
            }));
        }
    }

    private sealed class FakeHealthMonitorService : IHealthMonitorService
    {
        public Task<Result<LinkHealthState>> CheckAsync(bool isNgrokRunning, string? stampUrl, string? ngrokError, CancellationToken cancellationToken = default)
        {
            if (!isNgrokRunning || string.IsNullOrWhiteSpace(stampUrl))
            {
                return Task.FromResult(Result<LinkHealthState>.Ok(new LinkHealthState
                {
                    Indicator = LinkIndicator.Idle,
                    BadgeText = "Chưa có link",
                    StatusText = "Idle",
                    StampUrl = null,
                }));
            }

            return Task.FromResult(Result<LinkHealthState>.Ok(new LinkHealthState
            {
                Indicator = LinkIndicator.Healthy,
                BadgeText = "Đã có link",
                StatusText = "OK",
                StampUrl = stampUrl,
            }));
        }
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        public Task<Result<UpdateManifest?>> CheckForUpdatesAsync(UpdateConfig updateConfig, string currentVersion, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<UpdateManifest?>.Ok(null));
        }
    }
}
