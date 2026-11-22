using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;

namespace ZdC.App;

/// <summary>
/// 通知区域悬停提示。NotifyIcon.Text 有 63 字符上限，无法完整列出应用，因此用
/// 一个不抢焦点的 WPF 窗口复刻 Windows 11 默认提示的圆角、描边和阴影。
/// 内容只列当前正在直连的应用。
/// </summary>
internal sealed class TrayTooltip : Window
{
    private const int MaximumNamesPerLine = 4;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly TextBlock _text;
    private readonly Border _surface;

    public TrayTooltip()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsHitTestVisible = false;
        UseLayoutRounding = true;
        _text = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            LineHeight = 20,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 440,
        };
        _surface = new Border
        {
            Margin = new Thickness(9),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 9),
            Child = _text,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.18,
            },
        };
        Content = _surface;
        ApplyTheme();
    }

    /// <summary>第一行带“正在直连：”，之后每行最多四个应用名。</summary>
    internal static string FormatApplications(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return "当前没有正在直连的应用";
        var lines = new List<string>();
        for (var index = 0; index < names.Count; index += MaximumNamesPerLine)
        {
            var group = string.Join("、", names.Skip(index).Take(MaximumNamesPerLine));
            lines.Add(index == 0 ? "正在直连：" + group : group);
        }
        return string.Join("\n", lines);
    }

    public void ShowApplications(IReadOnlyList<string> names, System.Drawing.Point cursor)
    {
        ApplyTheme();
        _text.Text = FormatApplications(names);
        Show();
        UpdateLayout();
        var handle = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(handle, out var bounds)) return;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        var left = Math.Clamp(cursor.X - width / 2, screen.Left + 8, screen.Right - width - 8);
        var top = Math.Max(screen.Top + 8, cursor.Y - height - 12);
        SetWindowPos(handle, HwndTopmost, left, top, 0, 0, SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private void ApplyTheme()
    {
        var light = IsLightTheme();
        var background = new SolidColorBrush(light ? Color.FromRgb(0xF9, 0xF9, 0xF9) : Color.FromRgb(0x2B, 0x2B, 0x2B));
        var border = new SolidColorBrush(light ? Color.FromRgb(0xE5, 0xE5, 0xE5) : Color.FromRgb(0x3D, 0x3D, 0x3D));
        var foreground = new SolidColorBrush(light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xF2, 0xF2, 0xF2));
        background.Freeze();
        border.Freeze();
        foreground.Freeze();
        _surface.Background = background;
        _surface.BorderBrush = border;
        _text.Foreground = foreground;
    }

    private static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is not int theme || theme != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);
}
