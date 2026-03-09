using System.Net.Http;
using System.Text.Json;
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
            using var request = new HttpRequestMessage(HttpMethod.Get, healthUrl);
            request.Headers.TryAddWithoutValidation("ngrok-skip-browser-warning", "true");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode && IsHealthyJsonPayload(payload))
            {
                return Result<LinkHealthState>.Ok(new LinkHealthState
                {
                    Indicator = LinkIndicator.Healthy,
                    BadgeText = UiText.Get("BadgeLinkReadyText", "Da co link"),
                    StatusText = UiText.Get("StatusEndpointHealthy", "Endpoint hoat dong binh thuong."),
                    StampUrl = stampUrl,
                });
            }

            var mappedNgrokError = MapNgrokError(payload);
            if (!string.IsNullOrWhiteSpace(mappedNgrokError))
            {
                return Result<LinkHealthState>.Ok(new LinkHealthState
                {
                    Indicator = LinkIndicator.Error,
                    BadgeText = UiText.Get("BadgeLinkErrorText", "Link loi"),
                    StatusText = mappedNgrokError,
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

    private static bool IsHealthyJsonPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("success", out var successProp)
                && successProp.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static string MapNgrokError(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return string.Empty;
        }

        var text = payload.ToLowerInvariant();
        if (text.Contains("err_ngrok_8012")
            || text.Contains("failed to establish a connection to the upstream web service"))
        {
            return "Ngrok da tao duoc link nhung backend local chua san sang (ERR_NGROK_8012).";
        }

        if (text.Contains("err_ngrok_6024"))
        {
            return "Ngrok dang tra trang canh bao trinh duyet (ERR_NGROK_6024).";
        }

        if (text.Contains("<!doctype html") && text.Contains("ngrok"))
        {
            return "Public URL dang tra trang HTML loi thay vi JSON backend.";
        }

        return string.Empty;
    }
}
