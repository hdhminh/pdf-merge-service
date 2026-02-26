namespace PdfStampNgrokDesktop.Models;

public enum UpdateChannel
{
    Stable = 0,
    Beta = 1,
}

public sealed class BackendConfig
{
    public int Port { get; set; } = 3000;
}

public sealed class NgrokConfig
{
    public string Region { get; set; } = "ap";

    public bool AutoStart { get; set; } = true;
}

public sealed class UiConfig
{
    public bool AutoCopyOnGenerate { get; set; }
}

public sealed class SecurityConfig
{
    public int AutoLockMinutes { get; set; } = 10;
}

public sealed class UpdateConfig
{
    public bool Enabled { get; set; } = true;

    public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;

    public string GitHubRepoUrl { get; set; } = string.Empty;

    // Legacy fields retained for backward compatibility with old config schema.
    public string StableManifestUrl { get; set; } = string.Empty;

    public string BetaManifestUrl { get; set; } = string.Empty;
}

public sealed class NgrokProfile
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string EncryptedToken { get; set; } = string.Empty;
}

public sealed class AppConfig
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public BackendConfig Backend { get; set; } = new();

    public NgrokConfig Ngrok { get; set; } = new();

    public UiConfig Ui { get; set; } = new();

    public SecurityConfig Security { get; set; } = new();

    public UpdateConfig Update { get; set; } = new();

    public List<NgrokProfile> Profiles { get; set; } = [];

    public string? ActiveProfileId { get; set; }
}

// Legacy schema (v1) for migration.
public sealed class LegacyNgrokProfile
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;
}

public sealed class LegacyAppConfig
{
    public int BackendPort { get; set; } = 3000;

    public string NgrokRegion { get; set; } = "ap";

    public bool AutoStartNgrok { get; set; } = true;

    public List<LegacyNgrokProfile> NgrokProfiles { get; set; } = [];

    public string? ActiveNgrokProfileId { get; set; }
}
