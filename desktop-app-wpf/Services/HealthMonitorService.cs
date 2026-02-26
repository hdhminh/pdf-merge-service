using System.Net.Http;
using PdfStampNgrokDesktop.Core;

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
                BadgeText = "Chưa có link",
                StatusText = "Sẵn sàng. Chọn token rồi bấm 'Tạo link'.",
                StampUrl = null,
            });
        }

        if (!isNgrokRunning && !string.IsNullOrWhiteSpace(stampUrl))
        {
            return Result<LinkHealthState>.Ok(new LinkHealthState
            {
                Indicator = LinkIndicator.Error,
                BadgeText = "Link lỗi",
                StatusText = "Có link cũ nhưng ngrok đã dừng.",
                StampUrl = stampUrl,
            });
        }

        if (!string.IsNullOrWhiteSpace(ngrokError))
        {
            return Result<LinkHealthState>.Ok(new LinkHealthState
            {
                Indicator = LinkIndicator.Error,
                BadgeText = "Lỗi ngrok",
                StatusText = ngrokError,
                StampUrl = stampUrl,
            });
        }

        if (string.IsNullOrWhiteSpace(stampUrl))
        {
            return Result<LinkHealthState>.Ok(new LinkHealthState
            {
                Indicator = LinkIndicator.Degraded,
                BadgeText = "Đang tạo",
                StatusText = "ngrok đã chạy, đang chờ tunnel.",
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
                    BadgeText = "Đã có link",
                    StatusText = "Endpoint hoạt động bình thường.",
                    StampUrl = stampUrl,
                });
            }

            return Result<LinkHealthState>.Ok(new LinkHealthState
            {
                Indicator = LinkIndicator.Error,
                BadgeText = "Link lỗi",
                StatusText = $"Endpoint trả về mã {(int)response.StatusCode}.",
                StampUrl = stampUrl,
            });
        }
        catch
        {
            return Result<LinkHealthState>.Ok(new LinkHealthState
            {
                Indicator = LinkIndicator.Error,
                BadgeText = "Link lỗi",
                StatusText = "Không thể truy cập endpoint qua public URL.",
                StampUrl = stampUrl,
            });
        }
    }
}
