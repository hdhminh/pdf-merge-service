using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using PdfStampNgrokDesktop.Helpers;

namespace PdfStampNgrokDesktop;

public partial class GuideWindow : Window
{
    private readonly IReadOnlyList<GuideSlide> _slides =
    [
        new(
            UiText.Get("GuideStep1Title", "Buoc 1 - Dang ky hoac dang nhap tai khoan ngrok"),
            UiText.Get("GuideStep1Body", "Vui long mo lien ket ben duoi de dang ky tai khoan moi hoac dang nhap tai khoan ngrok hien co."),
            "https://dashboard.ngrok.com/signup",
            "Assets/guide-step-1.png"),
        new(
            UiText.Get("GuideStep2Title", "Buoc 2 - Lay AuthToken tu trang quan tri ngrok"),
            UiText.Get("GuideStep2Body", "Sau khi dang nhap, vao Dashboard -> Getting Started, roi copy chuoi ngay sau lenh \"ngrok config add-authtoken\"."),
            null,
            "Assets/guide-step-2.png"),
        new(
            UiText.Get("GuideStep3Title", "Buoc 3 - Them token vao phan mem"),
            UiText.Get("GuideStep3Body", "Tai man hinh Token cua phan mem: nhap ten profile, dan AuthToken vao o \"Nhap AuthToken\", sau do bam \"Them token\" va chon \"Dung\"."),
            null,
            "Assets/guide-step-3.png"),
        new(
            UiText.Get("GuideStep4Title", "Buoc 4 - Tao link va cap nhat Google Sheet tu dong"),
            UiText.Get("GuideStep4Body", "Bam \"Tao link\" de khoi dong tunnel. App se tu dong cap nhat endpoint vao o Google Sheet da cau hinh. Chi bam \"Copy\" khi ban can dan endpoint thu cong o noi khac."),
            null,
            "Assets/guide-step-4.png"),
        new(
            UiText.Get("GuideStep5Title", "Buoc 5 - Ky dung o ky doanh nghiep va o ky ca nhan"),
            UiText.Get("GuideStep5Body", "O lech trai la ky doanh nghiep (con dau). O phia duoi ben phai la ky ca nhan (cong chung vien)."),
            null,
            "Assets/guide-step-5.png"),
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
        StepIndexText.Text = UiText.Format("GuideStepIndexTemplate", "Buoc {0}/{1}", _currentIndex + 1, _slides.Count);
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
        NextButton.Content = _currentIndex >= _slides.Count - 1
            ? UiText.Get("GuideDoneButton", "Xong")
            : UiText.Get("GuideNextButton", "Sau");
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
