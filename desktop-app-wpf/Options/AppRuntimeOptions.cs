namespace PdfStampNgrokDesktop.Options;

public sealed class AppRuntimeOptions
{
    public string DataRootPath { get; set; } = string.Empty;

    public int RefreshIntervalSeconds { get; set; } = 5;

    public int ConfigBackupKeepCount { get; set; } = 30;

    public int LogRetentionFileCount { get; set; } = 14;

    public int TokenRevealSeconds { get; set; } = 20;

    public int MemoryWarningMb { get; set; } = 700;
}
