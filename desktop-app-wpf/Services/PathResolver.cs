using System.IO;

namespace PdfStampNgrokDesktop.Services;

internal static class PathResolver
{
    public static string ResolveRepoRoot()
    {
        static bool IsBackendRoot(string path)
        {
            return File.Exists(Path.Combine(path, "index.js"));
        }

        var fromEnv = Environment.GetEnvironmentVariable("BACKEND_ROOT")?.Trim();
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv) && IsBackendRoot(fromEnv))
        {
            return fromEnv;
        }

        static string? FindParentWithBackend(string startPath)
        {
            var current = new DirectoryInfo(startPath);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "index.js")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return null;
        }

        var appBase = FindParentWithBackend(AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(appBase))
        {
            return appBase;
        }

        var currentDir = FindParentWithBackend(Directory.GetCurrentDirectory());
        if (!string.IsNullOrWhiteSpace(currentDir))
        {
            return currentDir;
        }

        throw new DirectoryNotFoundException(
            "Khong tim thay backend (index.js). Hay chay app trong thu muc du an backend hoac cau hinh bien moi truong BACKEND_ROOT.");
    }

    public static string ResolveNgrokCommand(string repoRoot)
    {
        var fromEnv = Environment.GetEnvironmentVariable("NGROK_CMD")?.Trim();
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var candidates = new[]
        {
            Path.Combine(repoRoot, "desktop-app", "bin", "win32-x64", "ngrok.exe"),
            Path.Combine(AppContext.BaseDirectory, "bin", "win32-x64", "ngrok.exe"),
            Path.Combine(repoRoot, "bin", "win32-x64", "ngrok.exe"),
        };

        return candidates.FirstOrDefault(File.Exists) ?? "ngrok";
    }
}
