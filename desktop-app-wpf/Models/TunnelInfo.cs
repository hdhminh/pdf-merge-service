namespace PdfStampNgrokDesktop.Models;

public sealed class TunnelInfo
{
    public string PublicUrl { get; init; } = string.Empty;

    public string StampUrl { get; init; } = string.Empty;

    public string HealthUrl { get; init; } = string.Empty;
}
