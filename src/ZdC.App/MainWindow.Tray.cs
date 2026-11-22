using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using ZdC.Core;

namespace ZdC.App;

public partial class MainWindow
{
    private const int TrayTooltipHideDistance = 160;

    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Task? _shutdownTask;
    private TrayTooltip? _trayTooltip;
    private DispatcherTimer? _trayTooltipTimer;
    private System.Drawing.Point _trayTooltipAnchor;
    private bool _trayTooltipLoading;

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
                _trayMenu = CreateTrayMenu();
                _trayIcon = new Forms.NotifyIcon { Icon = icon, ContextMenuStrip = _trayMenu };
                _trayIcon.MouseClick += Tray_MouseClick;
                _trayIcon.MouseMove += Tray_MouseMove;
            }
            _trayIcon.Visible = true;
            Hide();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            StatusText.Text = ex.Message;
        }
    }

    private Forms.ContextMenuStrip CreateTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem("打开主界面")
        {
            Font = new System.Drawing.Font(System.Drawing.SystemFonts.MenuFont, System.Drawing.FontStyle.Bold),
        };
        open.Click += (_, _) => ShowFromTray();
        var exit = new Forms.ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitFromTray();
        menu.Items.Add(open);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exit);
        menu.Opening += (_, _) => HideTrayTooltip();
        return menu;
    }

    private void ShowFromTray()
    {
        HideTrayTooltip();
        if (_shutdownCompleted) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon is not null) _trayIcon.Visible = false;
    }

    private void ExitFromTray()
    {
        HideTrayTooltip();
        if (_shutdownCompleted) return;
        if (_busy || _applicationOperations.Count > 0)
        {
            Show();
            Activate();
            StatusText.Text = "正在完成当前操作，请稍后关闭。";
            return;
        }
        if (_trayIcon is not null) _trayIcon.Visible = false;
        Close();
    }

    private void Tray_MouseClick(object? sender, Forms.MouseEventArgs e)
    {
        HideTrayTooltip();
        if (e.Button != Forms.MouseButtons.Left || _shutdownCompleted) return;
        ShowFromTray();
    }

    private void Tray_MouseMove(object? sender, Forms.MouseEventArgs e)
    {
        if (_shutdownCompleted || _trayIcon is null || !_trayIcon.Visible) return;
        if (_trayTooltip is { IsVisible: true })
        {
            // 鼠标仍停在图标上时不断刷新锚点，避免提示窗被误判为“已离开”。
            _trayTooltipAnchor = Forms.Cursor.Position;
            return;
        }
        if (_trayTooltipLoading) return;
        _trayTooltipLoading = true;
        _ = ShowTrayTooltipAsync();
    }

    private async Task ShowTrayTooltipAsync()
    {
        try
        {
            var pointer = Forms.Cursor.Position;
            var names = await LoadDirectApplicationNamesAsync();
            if (_shutdownCompleted || _trayIcon is null || !_trayIcon.Visible || _shutdownTask is not null) return;
            var current = Forms.Cursor.Position;
            if (Math.Abs(current.X - pointer.X) > 80 || Math.Abs(current.Y - pointer.Y) > 80) return;
            _trayTooltip ??= new TrayTooltip();
            _trayTooltipAnchor = current;
            _trayTooltip.ShowApplications(names, current);
            _trayTooltipTimer ??= CreateTrayTooltipTimer();
            _trayTooltipTimer.Start();
        }
        finally { _trayTooltipLoading = false; }
    }

    private DispatcherTimer CreateTrayTooltipTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            if (_trayTooltip is not { IsVisible: true } || _trayIcon is not { Visible: true })
            {
                HideTrayTooltip();
                return;
            }
            var cursor = Forms.Cursor.Position;
            if (Math.Abs(cursor.X - _trayTooltipAnchor.X) > TrayTooltipHideDistance ||
                Math.Abs(cursor.Y - _trayTooltipAnchor.Y) > TrayTooltipHideDistance)
                HideTrayTooltip();
        };
        return timer;
    }

    private void HideTrayTooltip()
    {
        _trayTooltipTimer?.Stop();
        if (_trayTooltip is { IsVisible: true }) _trayTooltip.Hide();
    }

    /// <summary>悬停提示只列运行中且已观测到直连连接的应用。</summary>
    private async Task<IReadOnlyList<string>> LoadDirectApplicationNamesAsync()
    {
        var names = new List<string>();
        if (_shutdownCompleted) return names;
        foreach (var entry in _entries)
        {
            if (_shutdownCompleted) break;
            try
            {
                var state = await _applications.GetSessionStateAsync(entry.Path);
                if (state.IsRunning && state.RoutingEnabled &&
                    state.ConnectionStatus == ApplicationConnectionStatus.DirectObserved)
                    names.Add(entry.Name);
            }
            catch (Exception) { }
        }
        return names;
    }

    private Task StopSessionsAsync() => _shutdownTask ??= _applications.DisposeAsync().AsTask();

    private void Application_Exit(object? sender, ExitEventArgs e)
    {
        // WPF can shut down without waiting for an async Closing handler during logoff.
        try { StopSessionsAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError(ex.ToString()); }
        finally
        {
            HideTrayTooltip();
            if (_trayTooltip is not null)
            {
                _trayTooltip.Close();
                _trayTooltip = null;
            }
            _trayTooltipTimer = null;
            if (_trayMenu is not null)
            {
                _trayMenu.Dispose();
                _trayMenu = null;
            }
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
