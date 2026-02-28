using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using PdfStampNgrokDesktop.Core;
using PdfStampNgrokDesktop.Models;
using Serilog;

namespace PdfStampNgrokDesktop.Services;

public sealed class NgrokService : INgrokService
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    private Process? _ngrokProcess;
    private bool _expectedStop;

    public bool IsRunning => _ngrokProcess is { HasExited: false };

    public string? LastError { get; private set; }

    public event EventHandler<int>? Exited;

    public async Task<Result> StartAsync(int backendPort, string token, string region, bool restart, CancellationToken cancellationToken = default)
    {
        if (restart)
        {
            await StopAsync();
        }

        if (_ngrokProcess is { HasExited: false })
        {
            return Result.Ok();
        }

        try
        {
            var backendRoot = PathResolver.ResolveRepoRoot();
            var command = PathResolver.ResolveNgrokCommand(backendRoot);
            var args = BuildArguments(backendPort, token, region);

            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                WorkingDirectory = backendRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            LastError = null;
            _expectedStop = false;

            _ngrokProcess = new Process
            {
                StartInfo = psi,
                EnableRaisingEvents = true,
            };

            _ngrokProcess.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                {
                    return;
                }

                LastError = e.Data.Trim();
                Log.Warning("ngrok stderr: {Message}", LastError);
            };

            _ngrokProcess.Exited += (_, _) =>
            {
                if (_expectedStop)
                {
                    return;
                }

                var code = _ngrokProcess?.ExitCode ?? -1;
                LastError = $"ngrok dừng bất thường (code {code}).";
                Log.Warning("ngrok exited unexpectedly with code {Code}.", code);
                Exited?.Invoke(this, code);
            };

            _ngrokProcess.Start();
            _ngrokProcess.BeginOutputReadLine();
            _ngrokProcess.BeginErrorReadLine();
            await Task.Delay(350, cancellationToken);

            if (_ngrokProcess.HasExited)
            {
                return Result.Fail(ErrorCode.NgrokStartFailed, LastError ?? "Không thể chạy ngrok.");
            }

            Log.Information("ngrok started with PID {Pid}.", _ngrokProcess.Id);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ngrok start failed.");
            return Result.Fail(ErrorCode.NgrokStartFailed, $"Không thể chạy ngrok: {ex.Message}");
        }
    }

    public async Task<Result> StopAsync()
    {
        if (_ngrokProcess is null)
        {
            LastError = null;
            return Result.Ok();
        }

        try
        {
            if (!_ngrokProcess.HasExited)
            {
                _expectedStop = true;
                _ngrokProcess.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Stop ngrok threw an exception.");
        }

        await Task.Delay(120);
        _ngrokProcess.Dispose();
        _ngrokProcess = null;
        _expectedStop = false;
        LastError = null;
        Log.Information("ngrok stopped.");
        return Result.Ok();
    }

    public async Task<Result<TunnelInfo?>> GetCurrentTunnelAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("http://127.0.0.1:4040/api/tunnels", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Result<TunnelInfo?>.Ok(null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("tunnels", out var tunnels)
                || tunnels.ValueKind != JsonValueKind.Array)
            {
                return Result<TunnelInfo?>.Ok(null);
            }

            foreach (var item in tunnels.EnumerateArray())
            {
                if (!item.TryGetProperty("public_url", out var urlProp))
                {
                    continue;
                }

                var publicUrl = (urlProp.GetString() ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(publicUrl))
                {
                    continue;
                }

                if (!publicUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    && !publicUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return Result<TunnelInfo?>.Ok(new TunnelInfo
                {
                    PublicUrl = publicUrl,
                    StampUrl = publicUrl.TrimEnd('/') + "/api/pdf/stamp",
                    HealthUrl = publicUrl.TrimEnd('/') + "/health",
                });
            }

            return Result<TunnelInfo?>.Ok(null);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Warning(ex, "GetCurrentTunnelAsync failed.");
            return Result<TunnelInfo?>.Fail(ErrorCode.NgrokTunnelUnavailable, $"Không đọc được tunnel ngrok: {ex.Message}");
        }
    }

    private static string BuildArguments(int backendPort, string token, string region)
    {
        var args = $"http {backendPort} --log=stdout --authtoken {token}";
        if (!string.IsNullOrWhiteSpace(region))
        {
            args += $" --region {region}";
        }

        return args;
    }
}
