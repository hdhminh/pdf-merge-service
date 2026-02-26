using System.Diagnostics;
using System.IO;
using System.Net.Http;
using PdfStampNgrokDesktop.Core;
using Serilog;

namespace PdfStampNgrokDesktop.Services;

public sealed class BackendService : IBackendService
{
    private readonly HttpClient _httpClient;
    private Process? _backendProcess;

    public BackendService()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
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
            var repoRoot = PathResolver.ResolveRepoRoot();
            var backendEntry = Path.Combine(repoRoot, "index.js");
            if (!File.Exists(backendEntry))
            {
                return Result.Fail(ErrorCode.NotFound, $"Không tìm thấy file backend: {backendEntry}");
            }

            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "index.js",
                WorkingDirectory = repoRoot,
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
            using var response = await _httpClient.GetAsync($"http://127.0.0.1:{port}/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
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
}
