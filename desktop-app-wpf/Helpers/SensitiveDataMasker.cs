namespace PdfStampNgrokDesktop.Helpers;

public static class SensitiveDataMasker
{
    public static string MaskToken(string? token)
    {
        var value = (token ?? string.Empty).Trim();
        if (value.Length <= 8)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : "***";
        }

        return $"{value[..4]}...{value[^4..]}";
    }
}
