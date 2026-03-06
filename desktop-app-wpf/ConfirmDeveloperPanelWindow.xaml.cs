using System.Windows;

namespace PdfStampNgrokDesktop;

public partial class ConfirmDeveloperPanelWindow : Window
{
    public string EnteredPassword { get; private set; } = string.Empty;

    public ConfirmDeveloperPanelWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PasswordInput.Focus();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        EnteredPassword = PasswordInput.Password ?? string.Empty;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
