using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using PdfStampNgrokDesktop.Core;
using Serilog;

namespace PdfStampNgrokDesktop.Services;

public sealed class BackendService : IBackendService
{
    private readonly HttpClient _healthHttpClient;
    private readonly HttpClient _apiHttpClient;
    private Process? _backendProcess;

    public BackendService()
    {
        _healthHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        _apiHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
    }

    public bool IsRunning => _backendProcess is { HasExited: false };

    public event EventHandler<int>? Exited;

    public async Task<Result> EnsureStartedAsync(int port, CancellationToken cancellationToken = default)
    {
        if (await IsHealthyAsync(port, cancellationToken))
        {
            return Result.Ok();
        }

        if (_backendProcess is { HasExited: false })
        {
            var ready = await WaitForHealthyAsync(port, TimeSpan.FromSeconds(10), cancellationToken);
            return ready
                ? Result.Ok()
                : Result.Fail(ErrorCode.BackendHealthFailed, "Backend không phản hồi /health.");
        }

        try
        {
            var backendRoot = PathResolver.ResolveRepoRoot();
            var backendEntry = Path.Combine(backendRoot, "index.js");
            if (!File.Exists(backendEntry))
            {
                return Result.Fail(ErrorCode.NotFound, $"Không tìm thấy file backend: {backendEntry}");
            }

            var nodeCommand = PathResolver.ResolveNodeCommand(backendRoot);
            if (!PathResolver.IsCommandUsable(nodeCommand))
            {
                return Result.Fail(
                    ErrorCode.NotFound,
                    "Khong tim thay node runtime. Vui long cap nhat app ban moi nhat hoac cai Node.js, hoac dat NODE_CMD den node.exe.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = nodeCommand,
                Arguments = "index.js",
                WorkingDirectory = backendRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.EnvironmentVariables["PORT"] = port.ToString();

            _backendProcess = new Process
            {
                StartInfo = psi,
                EnableRaisingEvents = true,
            };

            _backendProcess.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                {
                    return;
                }

                Log.Warning("Backend stderr: {Error}", e.Data);
            };

            _backendProcess.Exited += (_, _) =>
            {
                Exited?.Invoke(this, _backendProcess?.ExitCode ?? -1);
            };

            _backendProcess.Start();
            _backendProcess.BeginErrorReadLine();
            _backendProcess.BeginOutputReadLine();
            Log.Information("Backend process started with PID {Pid}.", _backendProcess.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Backend start failed.");
            return Result.Fail(ErrorCode.BackendStartFailed, $"Không thể khởi động backend: {ex.Message}");
        }

        var healthy = await WaitForHealthyAsync(port, TimeSpan.FromSeconds(10), cancellationToken);
        return healthy
            ? Result.Ok()
            : Result.Fail(ErrorCode.BackendHealthFailed, "Backend khởi động nhưng /health không phản hồi.");
    }

    public async Task<Result> StopAsync()
    {
        if (_backendProcess is null)
        {
            return Result.Ok();
        }

        try
        {
            if (!_backendProcess.HasExited)
            {
                _backendProcess.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Stop backend threw an exception.");
        }

        await Task.Delay(120);
        _backendProcess.Dispose();
        _backendProcess = null;
        Log.Information("Backend stopped.");
        return Result.Ok();
    }

    public async Task<bool> IsHealthyAsync(int port, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _healthHttpClient.GetAsync($"http://127.0.0.1:{port}/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<Result> SyncGoogleSheetEndpointAsync(
        int port,
        string sheetId,
        string targetCellA1,
        string webhookUrl,
        string endpointUrl,
        CancellationToken cancellationToken = default)
    {
        var normalizedSheetId = (sheetId ?? string.Empty).Trim();
        var normalizedTargetCell = (targetCellA1 ?? string.Empty).Trim();
        var normalizedWebhookUrl = (webhookUrl ?? string.Empty).Trim();
        var normalizedEndpoint = (endpointUrl ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(normalizedSheetId))
        {
            return Result.Fail(ErrorCode.InvalidInput, "Google Sheet ID không được để trống.");
        }

        if (string.IsNullOrWhiteSpace(normalizedTargetCell))
        {
            normalizedTargetCell = "CONFIG!B32";
        }

        if (string.IsNullOrWhiteSpace(normalizedEndpoint))
        {
            return Result.Fail(ErrorCode.InvalidInput, "Endpoint không hợp lệ.");
        }

        try
        {
            Log.Information(
                "Sheet sync request: sheetId={SheetId}, targetA1={TargetA1}, webhook={WebhookUrl}, endpoint={Endpoint}",
                normalizedSheetId,
                normalizedTargetCell,
                normalizedWebhookUrl,
                normalizedEndpoint);

            using var response = await _apiHttpClient.PostAsJsonAsync(
                $"http://127.0.0.1:{port}/api/google-sheet/set-endpoint",
                new
                {
                    sheetId = normalizedSheetId,
                    targetA1 = normalizedTargetCell,
                    webhookUrl = normalizedWebhookUrl,
                    endpoint = normalizedEndpoint,
                },
                cancellationToken);

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var successPayload = TryParseBackendError(payload);
                if (successPayload.Success != true)
                {
                    return Result.Fail(
                        ErrorCode.GoogleSheetSyncFailed,
                        "Backend đồng bộ Sheet trả dữ liệu không hợp lệ (thiếu success=true).");
                }

                var upstreamWrittenValue = ExtractJsonString(payload, "upstream.writtenValue");
                if (!string.IsNullOrWhiteSpace(upstreamWrittenValue)
                    && !string.Equals(upstreamWrittenValue, normalizedEndpoint, StringComparison.Ordinal))
                {
                    return Result.Fail(
                        ErrorCode.GoogleSheetSyncFailed,
                        $"Webhook ghi khác endpoint yêu cầu. expected={normalizedEndpoint}, actual={upstreamWrittenValue}");
                }

                Log.Information(
                    "Sheet sync success: targetA1={TargetA1}, endpoint={Endpoint}, writtenValue={WrittenValue}",
                    normalizedTargetCell,
                    normalizedEndpoint,
                    upstreamWrittenValue ?? string.Empty);

                return Result.Ok();
            }

            var parsed = TryParseBackendError(payload);
            Log.Warning(
                "Sheet sync backend failed: status={Status}, code={Code}, message={Message}, payload={Payload}",
                (int)response.StatusCode,
                parsed.ErrorCode,
                parsed.Message,
                payload);
            if (ShouldFallbackToDirectWebhook(response.StatusCode, parsed))
            {
                var fallback = await SyncDirectToWebhookAsync(
                    normalizedWebhookUrl,
                    normalizedSheetId,
                    normalizedTargetCell,
                    normalizedEndpoint,
                    cancellationToken);
                if (fallback.IsSuccess)
                {
                    return Result.Ok("Đồng bộ qua webhook trực tiếp thành công.");
                }

                return fallback;
            }

            var code = string.Equals(parsed.ErrorCode, "GOOGLE_SHEET_SYNC_NOT_CONFIGURED", StringComparison.OrdinalIgnoreCase)
                ? ErrorCode.GoogleSheetSyncNotConfigured
                : ErrorCode.GoogleSheetSyncFailed;
            var message = string.IsNullOrWhiteSpace(parsed.Message)
                ? $"Backend trả lỗi đồng bộ Sheet ({(int)response.StatusCode})."
                : parsed.Message;
            return Result.Fail(code, message);
        }
        catch (TaskCanceledException)
        {
            return Result.Fail(ErrorCode.GoogleSheetSyncFailed, "Đồng bộ Google Sheet bị timeout.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Sync Google Sheet endpoint failed.");
            return Result.Fail(ErrorCode.GoogleSheetSyncFailed, $"Không thể đồng bộ Google Sheet: {ex.Message}");
        }
    }

    private async Task<bool> WaitForHealthyAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsHealthyAsync(port, cancellationToken))
            {
                return true;
            }

            await Task.Delay(300, cancellationToken);
        }

        return false;
    }

    private static BackendErrorPayload TryParseBackendError(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new BackendErrorPayload();
        }

        try
        {
            return JsonSerializer.Deserialize<BackendErrorPayload>(
                       raw,
                       new JsonSerializerOptions
                       {
                           PropertyNameCaseInsensitive = true,
                       })
                   ?? new BackendErrorPayload();
        }
        catch
        {
            return new BackendErrorPayload
            {
                Message = raw,
            };
        }
    }

    private static string? ExtractJsonString(string raw, string path)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var current = doc.RootElement;
            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                {
                    return null;
                }
            }

            if (current.ValueKind == JsonValueKind.String)
            {
                return current.GetString();
            }

            return current.ValueKind == JsonValueKind.Null ? null : current.ToString();
        }
        catch
        {
            return null;
        }
    }

    private async Task<Result> SyncDirectToWebhookAsync(
        string webhookUrl,
        string sheetId,
        string targetCellA1,
        string endpointUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return Result.Fail(ErrorCode.GoogleSheetSyncFailed, "Webhook Google Sheet chưa cấu hình.");
        }

        try
        {
            Log.Information(
                "Sheet sync direct webhook request: sheetId={SheetId}, targetA1={TargetA1}, webhook={WebhookUrl}, endpoint={Endpoint}",
                sheetId,
                targetCellA1,
                webhookUrl,
                endpointUrl);

            using var response = await _apiHttpClient.PostAsJsonAsync(
                webhookUrl,
                new
                {
                    sheetId,
                    targetA1 = targetCellA1,
                    endpoint = endpointUrl,
                },
                cancellationToken);

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Result.Fail(ErrorCode.GoogleSheetSyncFailed, BuildWebhookErrorMessage(response.StatusCode, payload));
            }

            var parsed = TryParseBackendError(payload);
            if (!string.IsNullOrWhiteSpace(parsed.ErrorCode)
                && string.Equals(parsed.ErrorCode, "INTERNAL_ERROR", StringComparison.OrdinalIgnoreCase))
            {
                return Result.Fail(ErrorCode.GoogleSheetSyncFailed, parsed.Message);
            }

            if (TryExtractSuccessFalse(parsed, payload, out var message))
            {
                return Result.Fail(ErrorCode.GoogleSheetSyncFailed, message);
            }

            if (parsed.Success != true)
            {
                return Result.Fail(
                    ErrorCode.GoogleSheetSyncFailed,
                    "Webhook không trả success=true. Kiểm tra đúng URL /exec của deployment web app.");
            }

            var writtenValue = ExtractJsonString(payload, "writtenValue");
            if (!string.IsNullOrWhiteSpace(writtenValue)
                && !string.Equals(writtenValue, endpointUrl, StringComparison.Ordinal))
            {
                return Result.Fail(
                    ErrorCode.GoogleSheetSyncFailed,
                    $"Webhook ghi khác endpoint yêu cầu. expected={endpointUrl}, actual={writtenValue}");
            }

            Log.Information(
                "Sheet sync direct webhook success: targetA1={TargetA1}, endpoint={Endpoint}, writtenValue={WrittenValue}",
                targetCellA1,
                endpointUrl,
                writtenValue ?? string.Empty);

            return Result.Ok();
        }
        catch (TaskCanceledException)
        {
            return Result.Fail(ErrorCode.GoogleSheetSyncFailed, "Webhook Google Sheet bị timeout.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Sync Google Sheet direct webhook failed.");
            return Result.Fail(ErrorCode.GoogleSheetSyncFailed, $"Không thể gọi webhook Google Sheet: {ex.Message}");
        }
    }

    private static bool ShouldFallbackToDirectWebhook(System.Net.HttpStatusCode statusCode, BackendErrorPayload payload)
    {
        if ((int)statusCode == 404 || (int)statusCode == 500 || (int)statusCode == 502 || (int)statusCode == 503 || (int)statusCode == 504)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(payload.Message)
            && payload.Message.IndexOf("upstream timeout", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return string.Equals(payload.ErrorCode, "GOOGLE_SHEET_SYNC_NOT_CONFIGURED", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildWebhookErrorMessage(System.Net.HttpStatusCode statusCode, string payload)
    {
        var parsed = TryParseBackendError(payload);
        if (!string.IsNullOrWhiteSpace(parsed.Message))
        {
            return parsed.Message;
        }

        if ((int)statusCode == 401 || (int)statusCode == 403
            || payload.IndexOf("không thể mở tệp", StringComparison.OrdinalIgnoreCase) >= 0
            || payload.IndexOf("couldn't open the file", StringComparison.OrdinalIgnoreCase) >= 0
            || payload.IndexOf("cannot open the file", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Webhook Apps Script chưa cho truy cập công khai. Vào Apps Script -> Deploy -> Manage deployments -> Edit -> Who has access: Anyone.";
        }

        return $"Webhook trả lỗi ({(int)statusCode}).";
    }

    private static bool TryExtractSuccessFalse(BackendErrorPayload payload, string raw, out string message)
    {
        message = string.Empty;

        if (payload.Success == false)
        {
            message = string.IsNullOrWhiteSpace(payload.Message)
                ? "Webhook Google Sheet trả về success=false."
                : payload.Message;
            return true;
        }

        // Fallback for non-standard payload text.
        if (raw.IndexOf("\"success\":false", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            message = string.IsNullOrWhiteSpace(payload.Message)
                ? "Webhook Google Sheet trả về success=false."
                : payload.Message;
            return true;
        }

        return false;
    }

    private sealed class BackendErrorPayload
    {
        public bool? Success { get; set; }

        public string ErrorCode { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;
    }
}
