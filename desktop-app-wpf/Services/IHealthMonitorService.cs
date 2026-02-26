using PdfStampNgrokDesktop.Core;

namespace PdfStampNgrokDesktop.Services;

public interface IHealthMonitorService
{
    Task<Result<LinkHealthState>> CheckAsync(
        bool isNgrokRunning,
        string? stampUrl,
        string? ngrokError,
        CancellationToken cancellationToken = default);
}
