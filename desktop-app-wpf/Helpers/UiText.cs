using System.Globalization;
using System.Windows;

namespace PdfStampNgrokDesktop.Helpers;

internal static class UiText
{
    public static string Get(string key, string fallback = "")
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is string value && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        catch
        {
            // Ignore resource lookup errors.
        }

        return fallback;
    }

    public static string Format(string key, string fallback, params object[] args)
    {
        var template = Get(key, fallback);
        return string.Format(CultureInfo.CurrentCulture, template, args);
    }
}
