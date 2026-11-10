using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MirrorDeck.ViewModels;

namespace MirrorDeck;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closing;
    private bool _readyToClose;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel(Dispatcher);
        DataContext = _viewModel;

        // A PasswordBox deliberately has no bindable property; it is synchronised by hand.
        PasswordInput.Password = _viewModel.Password;

        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        Activated += async (_, _) => await _viewModel.OnWindowActivatedAsync();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => _viewModel.Password = PasswordInput.Password;

    private void OnLogChanged(object sender, TextChangedEventArgs e)
    {
        // follow the log, unless the user has scrolled up to read something
        if (!LogBox.IsKeyboardFocusWithin) LogBox.ScrollToEnd();
    }

    /// <summary>
    /// Closing the window ends the receiver. The engine is asked to stop first, which needs a moment
    /// (it tells the network that this PC is no longer an AirPlay target), so the first close request is
    /// held back until that has finished.
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_readyToClose) return;

        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        Title = "MirrorDeck – stopping…";

        try
        {
            await _viewModel.ShutdownAsync();
        }
        catch (Exception)
        {
            // closing must always succeed; the engine is tied to our process and dies with it regardless
        }

        _readyToClose = true;
        Close();
    }
}
