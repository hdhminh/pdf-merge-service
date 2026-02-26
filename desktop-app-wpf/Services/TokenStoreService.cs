using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PdfStampNgrokDesktop.Core;
using PdfStampNgrokDesktop.Helpers;
using PdfStampNgrokDesktop.Models;
using PdfStampNgrokDesktop.Options;
using Serilog;

namespace PdfStampNgrokDesktop.Services;

public sealed class TokenStoreService : ITokenStoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly AppRuntimeOptions _runtimeOptions;

    public TokenStoreService(IOptions<AppRuntimeOptions> runtimeOptions)
    {
        _runtimeOptions = runtimeOptions.Value;
        var root = string.IsNullOrWhiteSpace(_runtimeOptions.DataRootPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PdfStampNgrokDesktop")
            : _runtimeOptions.DataRootPath.Trim();
        Directory.CreateDirectory(root);
        ConfigPath = Path.Combine(root, "app-config.json");
        BackupDirectoryPath = Path.Combine(root, "config-backups");
        Directory.CreateDirectory(BackupDirectoryPath);
    }

    public string ConfigPath { get; }

    public string BackupDirectoryPath { get; }

    public async Task<Result<AppConfig>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                var defaults = Normalize(new AppConfig());
                var saveResult = await SaveAsync(defaults, cancellationToken);
                if (!saveResult.IsSuccess)
                {
                    return Result<AppConfig>.Fail(saveResult.Code, saveResult.Message);
                }

                return Result<AppConfig>.Ok(defaults);
            }

            var raw = await File.ReadAllTextAsync(ConfigPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(raw))
            {
                var defaults = Normalize(new AppConfig());
                await SaveAsync(defaults, cancellationToken);
                return Result<AppConfig>.Ok(defaults);
            }

            AppConfig config;
            var migrated = false;

            using (var document = JsonDocument.Parse(raw))
            {
                var root = document.RootElement;
                if (root.TryGetProperty("Version", out var versionProp)
                    && versionProp.ValueKind == JsonValueKind.Number
                    && versionProp.GetInt32() >= AppConfig.CurrentVersion)
                {
                    config = JsonSerializer.Deserialize<AppConfig>(raw, JsonOptions) ?? new AppConfig();
                }
                else
                {
                    var legacy = JsonSerializer.Deserialize<LegacyAppConfig>(raw, JsonOptions) ?? new LegacyAppConfig();
                    config = await MigrateFromLegacyAsync(legacy, cancellationToken);
                    migrated = true;
                }
            }

            var normalized = Normalize(config);
            if (migrated)
            {
                Log.Information("Config migrated from legacy schema to version {Version}.", AppConfig.CurrentVersion);
                await SaveAsync(normalized, cancellationToken);
            }

            return Result<AppConfig>.Ok(normalized);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Load config failed.");
            return Result<AppConfig>.Fail(ErrorCode.SerializationFailure, $"Không thể đọc config: {ex.Message}");
        }
    }

    public async Task<Result> SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        try
        {
            var normalized = Normalize(config);

            if (File.Exists(ConfigPath))
            {
                await CreateBackupSnapshotAsync(cancellationToken);
            }

            var json = JsonSerializer.Serialize(normalized, JsonOptions) + Environment.NewLine;
            await File.WriteAllTextAsync(ConfigPath, json, cancellationToken);
            CleanupOldBackups();
            return Result.Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Save config failed.");
            return Result.Fail(ErrorCode.IoFailure, $"Không thể lưu config: {ex.Message}");
        }
    }

    public async Task<Result<AppConfig>> RestoreLatestBackupAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var latest = new DirectoryInfo(BackupDirectoryPath)
                .EnumerateFiles("app-config-*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (latest is null)
            {
                return Result<AppConfig>.Fail(ErrorCode.NotFound, "Không có backup config để khôi phục.");
            }

            await File.WriteAllTextAsync(ConfigPath, await File.ReadAllTextAsync(latest.FullName, cancellationToken), cancellationToken);
            return await LoadAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Restore config backup failed.");
            return Result<AppConfig>.Fail(ErrorCode.IoFailure, $"Khôi phục backup thất bại: {ex.Message}");
        }
    }

    public Result<string> ProtectToken(string plainToken)
    {
        try
        {
            var raw = Encoding.UTF8.GetBytes(plainToken.Trim());
            var protectedBytes = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
            return Result<string>.Ok(Convert.ToBase64String(protectedBytes));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Protect token failed.");
            return Result<string>.Fail(ErrorCode.EncryptionFailure, "Không thể mã hóa token.");
        }
    }

    public Result<string> UnprotectToken(string encryptedToken)
    {
        try
        {
            var protectedBytes = Convert.FromBase64String(encryptedToken.Trim());
            var raw = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var token = Encoding.UTF8.GetString(raw);
            return Result<string>.Ok(token);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unprotect token failed.");
            return Result<string>.Fail(ErrorCode.EncryptionFailure, "Không thể giải mã token.");
        }
    }

    private async Task<AppConfig> MigrateFromLegacyAsync(LegacyAppConfig legacy, CancellationToken cancellationToken)
    {
        await Task.Yield();
        var migrated = new AppConfig
        {
            Version = AppConfig.CurrentVersion,
            Backend = new BackendConfig { Port = legacy.BackendPort > 0 ? legacy.BackendPort : 3000 },
            Ngrok = new NgrokConfig
            {
                Region = string.IsNullOrWhiteSpace(legacy.NgrokRegion) ? "ap" : legacy.NgrokRegion.Trim(),
                AutoStart = legacy.AutoStartNgrok,
            },
            Profiles = [],
            ActiveProfileId = null,
        };

        foreach (var profile in legacy.NgrokProfiles ?? [])
        {
            var token = (profile.Token ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var protectedToken = ProtectToken(token);
            if (!protectedToken.IsSuccess || string.IsNullOrWhiteSpace(protectedToken.Value))
            {
                continue;
            }

            migrated.Profiles.Add(new NgrokProfile
            {
                Id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id.Trim(),
                Name = (profile.Name ?? string.Empty).Trim(),
                EncryptedToken = protectedToken.Value,
            });
        }

        migrated.ActiveProfileId = migrated.Profiles.Any(p => p.Id == (legacy.ActiveNgrokProfileId ?? string.Empty).Trim())
            ? legacy.ActiveNgrokProfileId
            : migrated.Profiles.FirstOrDefault()?.Id;

        return migrated;
    }

    private AppConfig Normalize(AppConfig input)
    {
        var output = new AppConfig
        {
            Version = AppConfig.CurrentVersion,
            Backend = new BackendConfig
            {
                Port = input.Backend?.Port > 0 ? input.Backend.Port : 3000,
            },
            Ngrok = new NgrokConfig
            {
                Region = string.IsNullOrWhiteSpace(input.Ngrok?.Region) ? "ap" : input.Ngrok.Region.Trim(),
                AutoStart = input.Ngrok?.AutoStart ?? true,
            },
            Ui = new UiConfig
            {
                AutoCopyOnGenerate = input.Ui?.AutoCopyOnGenerate ?? false,
            },
            Security = new SecurityConfig
            {
                AutoLockMinutes = input.Security?.AutoLockMinutes > 0 ? input.Security.AutoLockMinutes : 10,
            },
            Update = new UpdateConfig
            {
                Enabled = input.Update?.Enabled ?? true,
                Channel = ResolveUpdateChannel(input.Update),
                GitHubRepoUrl = ResolveUpdateRepoUrl(input.Update),
                StableManifestUrl = (input.Update?.StableManifestUrl ?? string.Empty).Trim(),
                BetaManifestUrl = (input.Update?.BetaManifestUrl ?? string.Empty).Trim(),
            },
            Profiles = [],
            ActiveProfileId = null,
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in input.Profiles ?? [])
        {
            var encrypted = (profile.EncryptedToken ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(encrypted) || !seen.Add(encrypted))
            {
                continue;
            }

            output.Profiles.Add(new NgrokProfile
            {
                Id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id.Trim(),
                Name = (profile.Name ?? string.Empty).Trim(),
                EncryptedToken = encrypted,
            });
        }

        var active = (input.ActiveProfileId ?? string.Empty).Trim();
        output.ActiveProfileId = output.Profiles.Any(p => p.Id == active) ? active : output.Profiles.FirstOrDefault()?.Id;
        return output;
    }

    private static string ResolveUpdateRepoUrl(UpdateConfig? update)
    {
        var current = (update?.GitHubRepoUrl ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(current))
        {
            return current;
        }

        var buildValue = BuildMetadata.UpdateRepoUrl;
        if (!string.IsNullOrWhiteSpace(buildValue))
        {
            return buildValue;
        }

        return string.Empty;
    }

    private static UpdateChannel ResolveUpdateChannel(UpdateConfig? update)
    {
        if (update is not null && Enum.IsDefined(typeof(UpdateChannel), update.Channel))
        {
            return update.Channel;
        }

        var buildValue = BuildMetadata.UpdateChannel;
        if (string.Equals(buildValue, "beta", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateChannel.Beta;
        }

        return UpdateChannel.Stable;
    }

    private async Task CreateBackupSnapshotAsync(CancellationToken cancellationToken)
    {
        var fileName = $"app-config-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
        var destination = Path.Combine(BackupDirectoryPath, fileName);
        var content = await File.ReadAllTextAsync(ConfigPath, cancellationToken);
        await File.WriteAllTextAsync(destination, content, cancellationToken);
    }

    private void CleanupOldBackups()
    {
        var keepCount = Math.Max(3, _runtimeOptions.ConfigBackupKeepCount);
        var files = new DirectoryInfo(BackupDirectoryPath)
            .EnumerateFiles("app-config-*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        if (files.Count <= keepCount)
        {
            return;
        }

        foreach (var file in files.Skip(keepCount))
        {
            try
            {
                file.Delete();
            }
            catch
            {
                // ignore
            }
        }
    }
}
