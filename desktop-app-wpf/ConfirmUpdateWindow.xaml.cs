using System.Windows;

namespace PdfStampNgrokDesktop;

public partial class ConfirmUpdateWindow : Window
{
    public ConfirmUpdateWindow(string version, bool fromBackgroundCheck)
    {
        InitializeComponent();
        var sourceText = fromBackgroundCheck ? "hệ thống phát hiện" : "bạn vừa kiểm tra";
        DataContext = new
        {
            TitleText = $"Có bản cập nhật {version}",
            MessageText = $"Đã có phiên bản mới ({version}) do {sourceText}. Bạn muốn tải và cập nhật ngay bây giờ không?",
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
