namespace PdfStampNgrokDesktop.Core;

public enum LinkIndicator
{
    Idle = 0,
    Healthy = 1,
    Degraded = 2,
    Error = 3,
}

public sealed class LinkHealthState
{
    public required LinkIndicator Indicator { get; init; }

    public required string BadgeText { get; init; }

    public required string StatusText { get; init; }

    public string? StampUrl { get; init; }
}
