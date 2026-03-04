using System.IO;

namespace PdfStampNgrokDesktop.Services;

internal static class PathResolver
{
    private const string BackendEntryFileName = "index.js";
    private const string BackendSubDirectoryName = "backend";
    private const string NodeExeFileName = "node.exe";
    private const string NgrokExeFileName = "ngrok.exe";
    private const string SignatureFieldToolExeFileName = "SignatureFieldTool.exe";

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

        static IEnumerable<string> GetSearchStartPaths()
        {
            if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
            {
                yield return AppContext.BaseDirectory;
            }

            var processPath = Environment.ProcessPath;
            var processDir = string.IsNullOrWhiteSpace(processPath) ? null : Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(processDir))
            {
                yield return processDir;
            }

            var currentDir = Directory.GetCurrentDirectory();
            if (!string.IsNullOrWhiteSpace(currentDir))
            {
                yield return currentDir;
            }

            var localCurrent = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PdfStampNgrokDesktop",
                "current");
            if (!string.IsNullOrWhiteSpace(localCurrent))
            {
                yield return localCurrent;
            }
        }

        var envCandidates = new[]
        {
            Environment.GetEnvironmentVariable("BACKEND_ROOT"),
            Environment.GetEnvironmentVariable("PDFSTAMP_HOME"),
            Environment.GetEnvironmentVariable("PDFSTAMP_APP_ROOT"),
        };
        foreach (var envCandidate in envCandidates)
        {
            var resolved = ResolveBackendRootFromPath(envCandidate);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
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

        foreach (var startPath in GetSearchStartPaths())
        {
            var resolved = FindParentWithBackend(startPath);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        throw new DirectoryNotFoundException(
            "Khong tim thay backend (index.js). Hay dat backend canh app (backend\\index.js), chay app trong thu muc du an backend, hoac cau hinh bien moi truong BACKEND_ROOT.");
    }

    public static string ResolveNodeCommand(string backendRoot)
    {
        var fromEnv = Environment.GetEnvironmentVariable("NODE_CMD")?.Trim();
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var appRoot = ResolveAppRoot(backendRoot);
        var candidates = new[]
        {
            Path.Combine(appRoot, "bin", "node-win-x64", NodeExeFileName),
            Path.Combine(AppContext.BaseDirectory, "bin", "node-win-x64", NodeExeFileName),
            Path.Combine(appRoot, "node", NodeExeFileName),
        };

        return candidates.FirstOrDefault(File.Exists) ?? "node";
    }

    public static string ResolveNgrokCommand(string backendRoot)
    {
        var fromEnv = Environment.GetEnvironmentVariable("NGROK_CMD")?.Trim();
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var appRoot = ResolveAppRoot(backendRoot);
        var repoRootParent = Directory.GetParent(backendRoot)?.FullName;
        var candidates = new[]
        {
            Path.Combine(appRoot, "bin", "win32-x64", NgrokExeFileName),
            Path.Combine(appRoot, "desktop-app", "bin", "win32-x64", NgrokExeFileName),
            string.IsNullOrWhiteSpace(repoRootParent)
                ? string.Empty
                : Path.Combine(repoRootParent, "bin", "win32-x64", NgrokExeFileName),
            Path.Combine(AppContext.BaseDirectory, "bin", "win32-x64", NgrokExeFileName),
            Path.Combine(backendRoot, "bin", "win32-x64", NgrokExeFileName),
        };

        return candidates.Where(static x => !string.IsNullOrWhiteSpace(x)).FirstOrDefault(File.Exists) ?? "ngrok";
    }

    public static string ResolveSignatureFieldToolCommand(string backendRoot)
    {
        var fromEnv = Environment.GetEnvironmentVariable("SIGNATURE_FIELD_TOOL_CMD")?.Trim();
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var appRoot = ResolveAppRoot(backendRoot);
        var candidates = new[]
        {
            Path.Combine(appRoot, "tools", "signature-field-tool", "SignatureFieldTool", "publish", "win-x64", SignatureFieldToolExeFileName),
            Path.Combine(AppContext.BaseDirectory, "tools", "signature-field-tool", "SignatureFieldTool", "publish", "win-x64", SignatureFieldToolExeFileName),
            Path.Combine(backendRoot, "tools", "signature-field-tool", "SignatureFieldTool", "publish", "win-x64", SignatureFieldToolExeFileName),
        };

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    public static bool IsCommandUsable(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        if (File.Exists(command))
        {
            return true;
        }

        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return false;
        }

        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        var extensions = (pathExt ?? ".EXE;.CMD;.BAT;.COM")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (extensions.Length == 0)
        {
            extensions = [".EXE"];
        }

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, command);
                if (File.Exists(candidate))
                {
                    return true;
                }

                foreach (var ext in extensions)
                {
                    var withExt = candidate.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                        ? candidate
                        : candidate + ext;
                    if (File.Exists(withExt))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Ignore invalid PATH entries.
            }
        }

        return false;
    }

    private static string ResolveAppRoot(string backendRoot)
    {
        if (string.IsNullOrWhiteSpace(backendRoot))
        {
            return AppContext.BaseDirectory;
        }

        var trimmed = backendRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        if (string.Equals(name, BackendSubDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            return Directory.GetParent(trimmed)?.FullName ?? trimmed;
        }

        return trimmed;
    }
}
