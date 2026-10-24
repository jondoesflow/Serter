using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Navigation;
using Serter.Core;

namespace Serter.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        GetSongBpmKey.Text = settings.GetSongBpmApiKey;
        LastFmKey.Text = settings.LastFmApiKey;
        CopyFormat.Text = settings.CopyFormat;
        BpmTolerance.Text = settings.BpmTolerancePercent.ToString(CultureInfo.CurrentCulture);
        MaxResults.Text = settings.MaxResults.ToString(CultureInfo.CurrentCulture);
        AlwaysOnTop.IsChecked = settings.AlwaysOnTop;
        HideClashes.IsChecked = settings.HideKeyClashes;
        HidePlayed.IsChecked = settings.HidePlayedThisSession;
        SeratoPath.Text = settings.SeratoPathOverride;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.GetSongBpmApiKey = GetSongBpmKey.Text.Trim();
        _settings.LastFmApiKey = LastFmKey.Text.Trim();
        _settings.CopyFormat = string.IsNullOrWhiteSpace(CopyFormat.Text) ? "{artist} {title}" : CopyFormat.Text;
        if (double.TryParse(BpmTolerance.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var tolerance))
            _settings.BpmTolerancePercent = Math.Clamp(tolerance, 1, 20);
        if (int.TryParse(MaxResults.Text, out var max))
            _settings.MaxResults = Math.Clamp(max, 5, 50);
        _settings.AlwaysOnTop = AlwaysOnTop.IsChecked == true;
        _settings.HideKeyClashes = HideClashes.IsChecked == true;
        _settings.HidePlayedThisSession = HidePlayed.IsChecked == true;
        _settings.SeratoPathOverride = Environment.ExpandEnvironmentVariables(SeratoPath.Text.Trim());
        DialogResult = true;
    }

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
