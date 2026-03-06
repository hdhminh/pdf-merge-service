using System.Windows;
using PdfStampNgrokDesktop.Helpers;

namespace PdfStampNgrokDesktop;

public partial class ConfirmUpdateWindow : Window
{
    public ConfirmUpdateWindow(string version, bool fromBackgroundCheck)
    {
        InitializeComponent();
        var titleText = UiText.Format("UpdateWindowTitleTemplate", "Co ban cap nhat {0}", version);
        var messageText = fromBackgroundCheck
            ? UiText.Format(
                "UpdateWindowMessageBackgroundTemplate",
                "Da co phien ban moi ({0}) do he thong phat hien. Ban muon tai va cap nhat ngay bay gio khong?",
                version)
            : UiText.Format(
                "UpdateWindowMessageManualTemplate",
                "Da co phien ban moi ({0}) do ban vua kiem tra. Ban muon tai va cap nhat ngay bay gio khong?",
                version);

        DataContext = new
        {
            TitleText = titleText,
            MessageText = messageText,
        };
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
