using System.Net.Http;
using PdfStampNgrokDesktop.Core;
using PdfStampNgrokDesktop.Helpers;

namespace PdfStampNgrokDesktop.Services;

public sealed class HealthMonitorService : IHealthMonitorService
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    public async Task<Result<LinkHealthState>> CheckAsync(
        bool isNgrokRunning,
        string? stampUrl,
        string? ngrokError,
        CancellationToken cancellationToken = default)
    {
        if (!isNgrokRunning && string.IsNullOrWhiteSpace(stampUrl))
        {
            return Result<LinkHealthState>.Ok(new LinkHealthState
            {
                Indicator = LinkIndicator.Idle,
                BadgeText = UiText.Get("IdleBadgeText", "Chua co link"),
                StatusText = UiText.Get("StatusReady", "San sang. Chon token roi bam 'Tao link'."),
                StampUrl = null,
            });
        }

        if (!isNgrokRunning && !string.IsNullOrWhiteSpace(stampUrl))
        {
            return Result<LinkHealthState>.Ok(new LinkHealthState
            {
                Indicator = LinkIndicator.Error,
                BadgeText = UiText.Get("BadgeLinkErrorText", "Link loi"),
                StatusText = UiText.Get("StatusStaleLinkNgrokStopped", "Co link cu nhung ngrok da dung."),
                StampUrl = stampUrl,
            });
        }

        if (!string.IsNullOrWhiteSpace(ngrokError))
        {
            return Result<LinkHealthState>.Ok(new LinkHealthState
            {
                Indicator = LinkIndicator.Error,
                BadgeText = UiText.Get("BadgeNgrokErrorText", "Loi ngrok"),
                StatusText = ngrokError,
                StampUrl = stampUrl,
            });
        }

        if (string.IsNullOrWhiteSpace(stampUrl))
        {
            return Result<LinkHealthState>.Ok(new LinkHealthState
            {
                Indicator = LinkIndicator.Degraded,
                BadgeText = UiText.Get("BadgeCreatingText", "Dang tao"),
                StatusText = UiText.Get("StatusWaitingTunnel", "ngrok da chay, dang cho tunnel."),
                StampUrl = null,
            });
        }

        var healthUrl = stampUrl.Replace("/api/pdf/stamp", "/health", StringComparison.OrdinalIgnoreCase);
        try
        {
            using var response = await _httpClient.GetAsync(healthUrl, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return Result<LinkHealthState>.Ok(new LinkHealthState
                {
                    Indicator = LinkIndicator.Healthy,
                    BadgeText = UiText.Get("BadgeLinkReadyText", "Da co link"),
                    StatusText = UiText.Get("StatusEndpointHealthy", "Endpoint hoat dong binh thuong."),
                    StampUrl = stampUrl,
                });
            }

            return Result<LinkHealthState>.Ok(new LinkHealthState
            {
                Indicator = LinkIndicator.Error,
                BadgeText = UiText.Get("BadgeLinkErrorText", "Link loi"),
                StatusText = UiText.Format("StatusEndpointCodeTemplate", "Endpoint tra ve ma {0}.", (int)response.StatusCode),
                StampUrl = stampUrl,
            });
        }
        catch
        {
            return Result<LinkHealthState>.Ok(new LinkHealthState
            {
                Indicator = LinkIndicator.Error,
                BadgeText = UiText.Get("BadgeLinkErrorText", "Link loi"),
                StatusText = UiText.Get("StatusEndpointUnreachable", "Khong the truy cap endpoint qua public URL."),
                StampUrl = stampUrl,
            });
        }
    }
}
