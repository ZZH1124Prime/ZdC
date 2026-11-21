using System.ComponentModel;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace ZdC.App;

public partial class MainWindow
{
    private Forms.NotifyIcon? _trayIcon;
    private Task? _shutdownTask;

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var menu = SettingsButton.ContextMenu;
        menu.PlacementTarget = SettingsButton;
        menu.IsOpen = !menu.IsOpen;
    }

    private void TrayOption_Click(object sender, RoutedEventArgs e)
    {
        try { TrayPreference.Save(TrayOption.IsChecked); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TrayOption.IsChecked = TrayPreference.Enabled;
            StatusText.Text = ex.Message;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // Only the title-bar button hides the window. Alt+F4 and system shutdown keep their exit semantics.
        if (!TrayOption.IsChecked || _shutdownTask is not null) { Close(); return; }
        try
        {
            if (_trayIcon is null)
            {
                var icon = System.Drawing.Icon.ExtractIcon(Path.Combine(AppContext.BaseDirectory, "ZdC.exe"), 0, true)
                    ?? throw new Win32Exception(1813);
                _trayIcon = new Forms.NotifyIcon { Icon = icon };
                _trayIcon.MouseClick += Tray_MouseClick;
            }
            _trayIcon.Visible = true;
            Hide();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void Tray_MouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left || _shutdownCompleted) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon is not null) _trayIcon.Visible = false;
    }

    private Task StopSessionsAsync() => _shutdownTask ??= _applications.DisposeAsync().AsTask();

    private void Application_Exit(object? sender, ExitEventArgs e)
    {
        // WPF can shut down without waiting for an async Closing handler during logoff.
        try { StopSessionsAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError(ex.ToString()); }
        finally
        {
            if (_trayIcon is not null)
            {
                var icon = _trayIcon.Icon;
                _trayIcon.Dispose();
                icon?.Dispose();
            }
        }
    }
}

/// <summary>The per-user marker lives alongside the application list and is independent of its save cycle.</summary>
internal static class TrayPreference
{
    private static readonly string Marker = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZdC", "close-to-tray");

    internal static bool Enabled => File.Exists(Marker);

    internal static void Save(bool enabled)
    {
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
            using var marker = new FileStream(Marker, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        }
        else
        {
            try { File.Delete(Marker); }
            catch (DirectoryNotFoundException) { }
        }
    }
}
