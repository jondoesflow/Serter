using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Navigation;
using Serter.Core;
using Serter.ViewModels;
using Serter.Views;

namespace Serter;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        var firstRun = !System.IO.File.Exists(AppSettings.FilePath);
        _settings = AppSettings.Load();
        _viewModel = new MainViewModel(_settings);
        DataContext = _viewModel;
        RestorePlacement();

        SourceInitialized += (_, _) => UseDarkTitleBar();
        Loaded += (_, _) =>
        {
            _viewModel.Start();
            if (firstRun)
                OnSettings(this, new RoutedEventArgs());
        };
        Closing += (_, _) =>
        {
            SavePlacement();
            _settings.Save();
            _viewModel.Dispose();
        };
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _settings.Save();
            _viewModel.ApplySettings();
            Topmost = _settings.AlwaysOnTop;
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        _viewModel.SearchManually(SearchBox.Text);
        SearchBox.Clear();
    }

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void RestorePlacement()
    {
        if (_settings.WindowWidth is > 200 && _settings.WindowHeight is > 200)
        {
            Width = _settings.WindowWidth.Value;
            Height = _settings.WindowHeight.Value;
        }
        if (_settings.WindowLeft is { } left && _settings.WindowTop is { } top
            && left >= SystemParameters.VirtualScreenLeft && top >= SystemParameters.VirtualScreenTop
            && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100
            && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            // First run: dock to the right edge of the screen, out of Serato's way.
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.WorkArea.Right - Width - 12;
            Top = SystemParameters.WorkArea.Top + 40;
        }
    }

    private void SavePlacement()
    {
        if (WindowState != WindowState.Normal)
            return;
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Dark title bar on Windows 10 20H1+ and Windows 11.</summary>
    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }
}
