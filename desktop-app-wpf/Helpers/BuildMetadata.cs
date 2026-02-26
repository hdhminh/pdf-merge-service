using System.Reflection;

namespace PdfStampNgrokDesktop.Helpers;

internal static class BuildMetadata
{
    public static string UpdateRepoUrl => ReadMetadata("UpdateRepoUrl");

    public static string UpdateChannel => ReadMetadata("UpdateChannel");

    private static string ReadMetadata(string key)
    {
        var value = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return (value ?? string.Empty).Trim();
    }
}
