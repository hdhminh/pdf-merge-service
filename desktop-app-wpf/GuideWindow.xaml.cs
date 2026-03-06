using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace PdfStampNgrokDesktop;

public partial class GuideWindow : Window
{
    private readonly IReadOnlyList<GuideSlide> _slides =
    [
        new(
            "Bước 1 - Đăng ký hoặc đăng nhập tài khoản ngrok",
            "Vui lòng mở liên kết bên dưới để đăng ký tài khoản mới hoặc đăng nhập tài khoản ngrok hiện có.",
            "https://dashboard.ngrok.com/signup",
            "Assets/guide-step-1.png"),
        new(
            "Bước 2 - Lấy AuthToken từ trang quản trị ngrok",
            "Sau khi đăng nhập, vào Dashboard -> Getting Started, rồi copy chuỗi ngay sau lệnh \"ngrok config add-authtoken\".",
            null,
            "Assets/guide-step-2.png"),
        new(
            "Bước 3 - Thêm token vào phần mềm",
            "Tại màn hình Token của phần mềm: nhập tên profile, dán AuthToken vào ô \"Nhập AuthToken\", sau đó bấm \"Thêm token\" và chọn \"Dùng\".",
            null,
            "Assets/guide-step-3.png"),
        new(
            "Bước 4 - Tạo link và cập nhật Google Sheet tự động",
            "Bấm \"Tạo link\" để khởi động tunnel. App sẽ tự động cập nhật endpoint vào ô Google Sheet đã cấu hình. Chỉ bấm \"Copy\" khi bạn cần dán endpoint thủ công ở nơi khác.",
            null,
            "Assets/guide-step-4.png"),
    ];

    private int _currentIndex;

    public GuideWindow()
    {
        InitializeComponent();
        PreviewKeyDown += GuideWindow_PreviewKeyDown;
        RenderSlide();
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex <= 0)
        {
            return;
        }

        _currentIndex--;
        RenderSlide();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex >= _slides.Count - 1)
        {
            Close();
            return;
        }

        _currentIndex++;
        RenderSlide();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void StepLinkHyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
            e.Handled = true;
        }
        catch
        {
            // Ignore browser launch errors.
        }
    }

    private void GuideWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                PrevButton_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Right:
                NextButton_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void RenderSlide()
    {
        var slide = _slides[_currentIndex];
        StepIndexText.Text = $"Bước {_currentIndex + 1}/{_slides.Count}";
        StepTitleText.Text = slide.Title;
        StepBodyText.Text = slide.Body;
        StepImage.Source = ResolveImageSource(slide.ImageRelativePath);

        if (string.IsNullOrWhiteSpace(slide.LinkUrl))
        {
            StepLinkBlock.Visibility = Visibility.Collapsed;
        }
        else
        {
            StepLinkRun.Text = slide.LinkUrl;
            StepLinkHyperlink.NavigateUri = new Uri(slide.LinkUrl, UriKind.Absolute);
            StepLinkBlock.Visibility = Visibility.Visible;
        }

        PrevButton.IsEnabled = _currentIndex > 0;
        NextButton.Content = _currentIndex >= _slides.Count - 1 ? "Xong" : "Sau";
    }

    private static ImageSource? ResolveImageSource(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            var uri = new Uri($"pack://application:,,,/{relativePath}", UriKind.Absolute);
            return new BitmapImage(uri);
        }
        catch
        {
            return null;
        }
    }

    private sealed record GuideSlide(
        string Title,
        string Body,
        string? LinkUrl,
        string? ImageRelativePath);
}
