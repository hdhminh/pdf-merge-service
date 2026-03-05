using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Serilog;

namespace PdfStampNgrokDesktop.Services;

internal static class SmokeSelfTestRunner
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public static async Task<int> RunAsync()
    {
        BackendService? backendService = null;
        try
        {
            var backendRoot = PathResolver.ResolveRepoRoot();
            var issues = PathResolver.CollectRuntimeIssues(backendRoot);
            if (issues.Count > 0)
            {
                Log.Error("Smoke test failed preflight: {Issues}", string.Join(", ", issues));
                return 2;
            }

            var port = ResolveSmokePort();
            backendService = new BackendService();
            var started = await backendService.EnsureStartedAsync(port);
            if (!started.IsSuccess)
            {
                Log.Error("Smoke test failed to start backend: {Code} {Message}", started.Code, started.Message);
                return 3;
            }

            using var healthResponse = await HttpClient.GetAsync($"http://127.0.0.1:{port}/health");
            if (!healthResponse.IsSuccessStatusCode)
            {
                Log.Error("Smoke test /health failed with status code {StatusCode}", (int)healthResponse.StatusCode);
                return 4;
            }

            var stampResponse = await RunStampSmokeAsync(port);
            if (!stampResponse.IsSuccess)
            {
                Log.Error("Smoke test stamp API failed: {Message}", stampResponse.Message);
                return 5;
            }

            Log.Information("Smoke test completed successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Smoke test crashed.");
            return 1;
        }
        finally
        {
            if (backendService is not null)
            {
                await backendService.StopAsync();
            }
        }
    }

    private static int ResolveSmokePort()
    {
        var raw = Environment.GetEnvironmentVariable("SMOKE_TEST_PORT");
        if (int.TryParse(raw, out var parsed) && parsed > 0 && parsed < 65535)
        {
            return parsed;
        }

        return 39030;
    }

    private static async Task<(bool IsSuccess, string Message)> RunStampSmokeAsync(int port)
    {
        var inputPdf = BuildMinimalPdfBytes();
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(inputPdf);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(fileContent, "file", "smoke.pdf");
        form.Add(new StringContent("SMOKE-001"), "certificationNumber");
        form.Add(new StringContent(DateTime.UtcNow.ToString("yyyy-MM-dd")), "certificationDate");

        using var response = await HttpClient.PostAsync($"http://127.0.0.1:{port}/api/pdf/stamp", form);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            return (false, $"status={(int)response.StatusCode}, body={errorBody}");
        }

        var outputBytes = await response.Content.ReadAsByteArrayAsync();
        if (outputBytes.Length < 5)
        {
            return (false, "output PDF is empty");
        }

        if (!IsPdfHeader(outputBytes))
        {
            return (false, "output payload is not a PDF");
        }

        return (true, string.Empty);
    }

    private static bool IsPdfHeader(IReadOnlyList<byte> bytes)
    {
        return bytes[0] == (byte)'%' &&
               bytes[1] == (byte)'P' &&
               bytes[2] == (byte)'D' &&
               bytes[3] == (byte)'F' &&
               bytes[4] == (byte)'-';
    }

    private static byte[] BuildMinimalPdfBytes()
    {
        const string stream = "BT\n/F1 12 Tf\n20 80 Td\n(SMOKE TEST) Tj\nET\n";
        var streamLength = Encoding.ASCII.GetByteCount(stream);
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 120] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {streamLength} >>\nstream\n{stream}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append(i + 1);
            sb.Append(" 0 obj\n");
            sb.Append(objects[i]);
            sb.Append("\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append("xref\n");
        sb.Append("0 ");
        sb.Append(objects.Length + 1);
        sb.Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (var i = 1; i < offsets.Count; i++)
        {
            sb.Append(offsets[i].ToString("D10"));
            sb.Append(" 00000 n \n");
        }

        sb.Append("trailer\n");
        sb.Append("<< /Size ");
        sb.Append(objects.Length + 1);
        sb.Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n");
        sb.Append(xrefOffset);
        sb.Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
