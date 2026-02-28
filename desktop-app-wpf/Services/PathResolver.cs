using System.IO;

namespace PdfStampNgrokDesktop.Services;

internal static class PathResolver
{
    private const string BackendEntryFileName = "index.js";
    private const string BackendSubDirectoryName = "backend";

    public static string ResolveRepoRoot()
    {
        static bool IsBackendRoot(string path)
        {
            return File.Exists(Path.Combine(path, BackendEntryFileName));
        }

        static string? ResolveBackendRootFromPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var trimmed = path.Trim();
            if (!Directory.Exists(trimmed))
            {
                return null;
            }

            if (IsBackendRoot(trimmed))
            {
                return trimmed;
            }

            var nestedBackend = Path.Combine(trimmed, BackendSubDirectoryName);
            return IsBackendRoot(nestedBackend) ? nestedBackend : null;
        }

        var fromEnv = Environment.GetEnvironmentVariable("BACKEND_ROOT")?.Trim();
        var resolvedFromEnv = ResolveBackendRootFromPath(fromEnv);
        if (!string.IsNullOrWhiteSpace(resolvedFromEnv))
        {
            return resolvedFromEnv;
        }

        static string? FindParentWithBackend(string? startPath)
        {
            if (string.IsNullOrWhiteSpace(startPath) || !Directory.Exists(startPath))
            {
                return null;
            }

            var current = new DirectoryInfo(startPath);
            while (current is not null)
            {
                var resolved = ResolveBackendRootFromPath(current.FullName);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
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
            "Khong tim thay backend (index.js). Hay dat backend canh app (backend\\index.js), chay app trong thu muc du an backend, hoac cau hinh bien moi truong BACKEND_ROOT.");
    }

    public static string ResolveNgrokCommand(string repoRoot)
    {
        var fromEnv = Environment.GetEnvironmentVariable("NGROK_CMD")?.Trim();
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var repoRootParent = Directory.GetParent(repoRoot)?.FullName;
        var candidates = new[]
        {
            Path.Combine(repoRoot, "desktop-app", "bin", "win32-x64", "ngrok.exe"),
            string.IsNullOrWhiteSpace(repoRootParent)
                ? string.Empty
                : Path.Combine(repoRootParent, "bin", "win32-x64", "ngrok.exe"),
            Path.Combine(AppContext.BaseDirectory, "bin", "win32-x64", "ngrok.exe"),
            Path.Combine(repoRoot, "bin", "win32-x64", "ngrok.exe"),
        };

        return candidates.Where(static x => !string.IsNullOrWhiteSpace(x)).FirstOrDefault(File.Exists) ?? "ngrok";
    }
}
