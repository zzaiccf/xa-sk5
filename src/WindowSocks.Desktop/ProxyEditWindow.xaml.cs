using System;
using System.Globalization;
using System.Windows;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public partial class ProxyEditWindow : Window
{
    private readonly ProxyProfile profile;
    public EditProxyRequest? Request { get; private set; }
    public ProxyEditWindow(ProxyProfile profile)
    {
        this.profile = profile; InitializeComponent();
        NodeName.Text = profile.Name; Host.Text = profile.Host; Port.Text = profile.Port.ToString(CultureInfo.InvariantCulture);
        Username.Text = profile.Username; Expiration.SelectedDate = profile.ExpiresOn?.ToDateTime(TimeOnly.MinValue);
    }
    private void ClearPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (NewPassword is null) return;
        NewPassword.IsEnabled = ClearPassword.IsChecked != true;
        if (!NewPassword.IsEnabled) NewPassword.Clear();
    }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try {
            if (!int.TryParse(Port.Text, out int port)) throw new InvalidOperationException("請輸入有效端口（1–65535）。");
            if (!string.IsNullOrWhiteSpace(Expiration.Text) && Expiration.SelectedDate is null) throw new InvalidOperationException("請輸入有效到期日期，或留空。");
            Request = new(profile.Id, NodeName.Text, Host.Text, port, Username.Text,
                ClearPassword.IsChecked == true ? "" : NewPassword.Password.Length > 0 ? NewPassword.Password : null,
                Expiration.SelectedDate is { } date ? DateOnly.FromDateTime(date) : null);
            // Secrets are kept in the backend; validate visible fields here without retrieving the saved password.
            ProxyEditing.Prepare([profile], Request);
            DialogResult = true;
        } catch (InvalidOperationException ex) { Error.Text = ex.Message; Request = null; }
    }
}
