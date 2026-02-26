using System.Net.Http;
using PdfStampNgrokDesktop.Core;
using PdfStampNgrokDesktop.Helpers;
using PdfStampNgrokDesktop.Models;
using Serilog;
using Velopack;
using Velopack.Sources;

namespace PdfStampNgrokDesktop.Services;

public sealed class UpdateService : IUpdateService
{
    public async Task<Result<UpdateManifest?>> CheckForUpdatesAsync(UpdateConfig updateConfig, string currentVersion, CancellationToken cancellationToken = default)
    {
        if (!updateConfig.Enabled)
        {
            return Result<UpdateManifest?>.Ok(null, "Auto-update dang tat.");
        }

        var repoUrl = ResolveRepoUrl(updateConfig);
        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            return Result<UpdateManifest?>.Ok(null, "Chua cau hinh GitHub repo cho auto-update.");
        }

        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out _))
        {
            return Result<UpdateManifest?>.Ok(null, "Bo qua auto-update vi URL repo khong hop le.");
        }

        var includePrerelease = updateConfig.Channel == UpdateChannel.Beta;

        try
        {
            var source = new GithubSource(repoUrl, string.Empty, includePrerelease);
            var manager = new UpdateManager(source);

            if (!manager.IsInstalled)
            {
                Log.Information("Skip auto-update because app is not Velopack-installed.");
                return Result<UpdateManifest?>.Ok(null, "Ban dev/publish roi, khong check auto-update.");
            }

            var updateInfo = await manager.CheckForUpdatesAsync();
            if (updateInfo is null)
            {
                return Result<UpdateManifest?>.Ok(null, "Da la phien ban moi nhat.");
            }

            await manager.DownloadUpdatesAsync(updateInfo, null, cancellationToken);

            var target = updateInfo.TargetFullRelease;
            var manifest = new UpdateManifest
            {
                Version = target.Version?.ToString() ?? currentVersion,
                DownloadUrl = repoUrl,
                Notes = target.NotesMarkdown ?? string.Empty,
            };

            Log.Information("Update downloaded. Applying and restarting to version {Version}.", manifest.Version);
            manager.ApplyUpdatesAndRestart(updateInfo);
            return Result<UpdateManifest?>.Ok(manifest, "Dang ap dung cap nhat.");
        }
        catch (OperationCanceledException)
        {
            return Result<UpdateManifest?>.Ok(null, "Da huy kiem tra cap nhat.");
        }
        catch (HttpRequestException ex)
        {
            Log.Information(ex, "Auto-update feed not available. Skip this check.");
            return Result<UpdateManifest?>.Ok(null, "Khong co feed cap nhat hoac chua release.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto-update check/apply failed.");
            return Result<UpdateManifest?>.Fail(ErrorCode.UpdateCheckFailed, $"Loi auto-update: {ex.Message}");
        }
    }

    private static string ResolveRepoUrl(UpdateConfig updateConfig)
    {
        var configured = (updateConfig.GitHubRepoUrl ?? string.Empty).Trim();
        var fromBuild = BuildMetadata.UpdateRepoUrl;
        var value = !string.IsNullOrWhiteSpace(configured) ? configured : fromBuild;

        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (value.Contains("your-org/your-repo", StringComparison.OrdinalIgnoreCase)
            || value.Contains("example/example", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return value;
    }
}
