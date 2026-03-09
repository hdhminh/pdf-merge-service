using System.Diagnostics;
using System.IO;
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
            if (!PathResolver.IsCommandUsable(command))
            {
                return Result.Fail(
                    ErrorCode.NotFound,
                    "Khong tim thay ngrok.exe. Vui long cap nhat app ban moi nhat hoac dat NGROK_CMD den duong dan ngrok.exe.");
            }

            // Cleanup stale ngrok processes from the same bundled binary path.
            KillStaleNgrokProcesses(command);

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

            var process = new Process
            {
                StartInfo = psi,
                EnableRaisingEvents = true,
            };
            _ngrokProcess = process;

            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                {
                    return;
                }

                var line = e.Data.Trim();
                if (!IsNgrokErrorLine(line))
                {
                    return;
                }

                LastError = NormalizeNgrokError(line);
                Log.Warning("ngrok stderr: {Message}", LastError);
                TryWriteDevConsole($"[ngrok:stderr] {LastError}");
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                {
                    return;
                }

                var line = e.Data.Trim();
                Log.Information("ngrok stdout: {Message}", line);
                TryWriteDevConsole($"[ngrok] {line}");
            };

            process.Exited += (_, _) =>
            {
                if (_expectedStop)
                {
                    return;
                }

                var code = process.ExitCode;
                if (string.IsNullOrWhiteSpace(LastError))
                {
                    LastError = $"ngrok dừng bất thường (code {code}).";
                }
                Log.Warning("ngrok exited unexpectedly with code {Code}.", code);
                Exited?.Invoke(this, code);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await Task.Delay(350, cancellationToken);

            if (process.HasExited)
            {
                return Result.Fail(ErrorCode.NgrokStartFailed, LastError ?? "Không thể chạy ngrok.");
            }

            Log.Information("ngrok started with PID {Pid}.", process.Id);
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
        if (_ngrokProcess is not null)
        {
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
        }

        try
        {
            var backendRoot = PathResolver.ResolveRepoRoot();
            var command = PathResolver.ResolveNgrokCommand(backendRoot);
            KillStaleNgrokProcesses(command);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cleanup stale ngrok processes failed.");
        }

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

                LastError = null;
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
        var args = $"http {backendPort} --log=stdout --inspect=false --authtoken {token}";
        if (!string.IsNullOrWhiteSpace(region))
        {
            args += $" --region {region}";
        }

        return args;
    }

    private static void TryWriteDevConsole(string line)
    {
        try
        {
            Console.WriteLine(line);
        }
        catch
        {
            // WPF release mode may not have an attached console.
        }
    }

    private static bool IsNgrokErrorLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var text = line.ToLowerInvariant();
        if (text.Contains("lvl=eror") || text.Contains("lvl=error"))
        {
            return true;
        }

        if (text.Contains("invalid authtoken")
            || text.Contains("authentication failed")
            || text.Contains("failed")
            || text.Contains("panic")
            || text.Contains("cannot")
            || text.Contains("unable"))
        {
            return true;
        }

        return false;
    }

    private static void KillStaleNgrokProcesses(string ngrokCommandPath)
    {
        var normalizedTarget = NormalizePath(ngrokCommandPath);
        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            return;
        }

        foreach (var process in Process.GetProcessesByName("ngrok"))
        {
            try
            {
                var processPath = string.Empty;
                try
                {
                    processPath = process.MainModule?.FileName ?? string.Empty;
                }
                catch
                {
                    // Ignore inaccessible processes.
                }

                if (!string.Equals(NormalizePath(processPath), normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
                Log.Warning("Killed stale ngrok process PID {Pid}.", process.Id);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Unable to kill stale ngrok process PID {Pid}.", process.Id);
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    private static string NormalizeNgrokError(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "Khong the chay ngrok.";
        }

        var text = line.ToLowerInvariant();
        if (text.Contains("err_ngrok_108")
            || text.Contains("1 simultaneous ngrok agent sessions")
            || text.Contains("authentication failed: your account is limited to 1 simultaneous"))
        {
            return "Tai khoan ngrok dang chay o noi khac (ERR_NGROK_108). Hay tat phien ngrok cu tai https://dashboard.ngrok.com/agents roi bam 'Tao link' lai.";
        }

        if (text.Contains("already online") && text.Contains("endpoint"))
        {
            return "Domain ngrok dang online o phien khac. Hay tat endpoint cu tai https://dashboard.ngrok.com/endpoints (hoac agents) roi bam 'Tao link' lai.";
        }

        if (text.Contains("err_ngrok_8012")
            || text.Contains("failed to establish a connection to the upstream web service"))
        {
            return "Ngrok da len tunnel nhung backend local khong ket noi (ERR_NGROK_8012). Hay khoi dong lai backend.";
        }

        if (text.Contains("err_ngrok_6024"))
        {
            return "Tunnel ngrok yeu cau xac minh trinh duyet (ERR_NGROK_6024). Ung dung da bo qua bang header, vui long thu lai.";
        }

        return "Khong the ket noi ngrok. Vui long tao lai link.";
    }

}

