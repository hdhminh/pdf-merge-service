using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using PdfStampNgrokDesktop.ViewModels;

namespace PdfStampNgrokDesktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _allowClose;
    private bool _isClosingInProgress;
    private bool _forceCloseAfterShutdown;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_forceCloseAfterShutdown)
        {
            e.Cancel = false;
            return;
        }

        if (_isClosingInProgress)
        {
            e.Cancel = true;
            return;
        }

        if (!_allowClose)
        {
            var dialog = new ConfirmCloseWindow
            {
                Owner = this,
            };

            var result = dialog.ShowDialog();
            if (result != true)
            {
                e.Cancel = true;
                return;
            }

            _allowClose = true;
        }

        e.Cancel = true;
        _isClosingInProgress = true;
        await _viewModel.ShutdownAsync();
        _forceCloseAfterShutdown = true;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            Close();
        }));
    }

    private void Window_OnUserActivity(object sender, InputEventArgs e)
    {
        _viewModel.NotifyUserActivity();
    }
}
