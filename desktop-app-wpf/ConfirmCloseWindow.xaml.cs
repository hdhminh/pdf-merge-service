using System.Windows;

namespace PdfStampNgrokDesktop;

public partial class ConfirmCloseWindow : Window
{
    public ConfirmCloseWindow()
    {
        InitializeComponent();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
