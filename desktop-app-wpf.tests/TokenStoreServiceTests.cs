using System.Text.Json;
using Microsoft.Extensions.Options;
using PdfStampNgrokDesktop.Models;
using PdfStampNgrokDesktop.Options;
using PdfStampNgrokDesktop.Services;

namespace PdfStampNgrokDesktop.Tests;

public sealed class TokenStoreServiceTests
{
    [Fact]
    public void ProtectAndUnprotectToken_Roundtrip_Succeeds()
    {
        var tempRoot = CreateTempRoot();
        var service = CreateService(tempRoot);

        var protect = service.ProtectToken("2onW-very-secret-token");
        Assert.True(protect.IsSuccess);
        Assert.NotNull(protect.Value);
        Assert.NotEqual("2onW-very-secret-token", protect.Value);

        var unprotect = service.UnprotectToken(protect.Value!);
        Assert.True(unprotect.IsSuccess);
        Assert.Equal("2onW-very-secret-token", unprotect.Value);
    }

    [Fact]
    public async Task LoadAsync_MigratesLegacyConfigToVersion2()
    {
        var tempRoot = CreateTempRoot();
        var service = CreateService(tempRoot);

        var legacy = new LegacyAppConfig
        {
            BackendPort = 3000,
            NgrokRegion = "ap",
            AutoStartNgrok = true,
            ActiveNgrokProfileId = "p1",
            NgrokProfiles =
            [
                new LegacyNgrokProfile
                {
                    Id = "p1",
                    Name = "Admin",
                    Token = "2onW-legacy-token",
                },
            ],
        };

        var rawLegacy = JsonSerializer.Serialize(legacy);
        await File.WriteAllTextAsync(service.ConfigPath, rawLegacy);

        var load = await service.LoadAsync();
        Assert.True(load.IsSuccess);
        Assert.NotNull(load.Value);
        Assert.Equal(AppConfig.CurrentVersion, load.Value!.Version);
        Assert.Single(load.Value.Profiles);
        Assert.Equal("p1", load.Value.ActiveProfileId);

        var encrypted = load.Value.Profiles[0].EncryptedToken;
        Assert.DoesNotContain("legacy-token", encrypted, StringComparison.OrdinalIgnoreCase);

        var unprotect = service.UnprotectToken(encrypted);
        Assert.True(unprotect.IsSuccess);
        Assert.Equal("2onW-legacy-token", unprotect.Value);
    }

    private static TokenStoreService CreateService(string root)
    {
        return new TokenStoreService(Microsoft.Extensions.Options.Options.Create(new AppRuntimeOptions
        {
            DataRootPath = root,
            ConfigBackupKeepCount = 5,
        }));
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"PdfStampNgrokDesktopTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
