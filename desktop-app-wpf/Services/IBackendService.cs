using PdfStampNgrokDesktop.Core;

namespace PdfStampNgrokDesktop.Services;

public interface IBackendService
{
    bool IsRunning { get; }

    event EventHandler<int>? Exited;

    Task<Result> EnsureStartedAsync(int port, CancellationToken cancellationToken = default);

    Task<Result> StopAsync();

    Task<bool> IsHealthyAsync(int port, CancellationToken cancellationToken = default);
}
