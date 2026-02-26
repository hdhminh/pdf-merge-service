namespace PdfStampNgrokDesktop.ViewModels;

public sealed class ProfileItemViewModel
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string MaskedToken { get; init; } = string.Empty;

    public string Display => $"{Name} ({MaskedToken})";
}
