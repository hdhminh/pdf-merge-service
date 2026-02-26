using PdfStampNgrokDesktop.Core;
using PdfStampNgrokDesktop.Models;

namespace PdfStampNgrokDesktop.Services;

public interface INgrokService
{
    bool IsRunning { get; }

    string? LastError { get; }

    event EventHandler<int>? Exited;

    Task<Result> StartAsync(int backendPort, string token, string region, bool restart, CancellationToken cancellationToken = default);

    Task<Result> StopAsync();

    Task<Result<TunnelInfo?>> GetCurrentTunnelAsync(CancellationToken cancellationToken = default);
}
