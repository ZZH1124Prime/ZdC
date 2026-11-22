using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;

namespace ZdC.App;

/// <summary>
/// 通知区域悬停提示。NotifyIcon.Text 有 63 字符上限，无法完整列出应用，因此用
/// 一个不抢焦点的 WPF 窗口复刻 Windows 11 默认提示的圆角、描边和阴影。
/// </summary>
internal sealed class TrayTooltip : Window
{
    private const int MaximumItemsPerLine = 4;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly TextBlock _text;
    private readonly Border _surface;
    private Brush _foreground = Brushes.Black;
    private Brush _muted = Brushes.Gray;
    private Brush _success = Brushes.Green;
    private Brush _danger = Brushes.Red;

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

    /// <summary>纯文本形式便于校验：第一行标题，之后每行最多四项，每项为“名称 状态”。</summary>
    internal static string FormatApplications(IReadOnlyList<string> names, IReadOnlyList<string> states)
    {
        if (names.Count == 0) return "没有正在运行的登记应用";
        var lines = new List<string> { "直连状态" };
        for (var index = 0; index < names.Count; index += MaximumItemsPerLine)
        {
            var count = Math.Min(MaximumItemsPerLine, names.Count - index);
            var items = new string[count];
            for (var offset = 0; offset < count; offset++)
                items[offset] = names[index + offset] + " " + states[index + offset];
            lines.Add(string.Join("、", items));
        }
        return string.Join("\n", lines);
    }

    public void ShowApplications(IReadOnlyList<string> names, IReadOnlyList<string> states,
        System.Drawing.Point cursor)
    {
        ApplyTheme();
        Render(names, states);
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

    private void Render(IReadOnlyList<string> names, IReadOnlyList<string> states)
    {
        _text.Inlines.Clear();
        if (names.Count == 0)
        {
            _text.Inlines.Add(new Run("没有正在运行的登记应用") { Foreground = _muted });
            return;
        }
        _text.Inlines.Add(new Run("直连状态") { Foreground = _muted, FontSize = 12 });
        for (var index = 0; index < names.Count; index += MaximumItemsPerLine)
        {
            _text.Inlines.Add(new LineBreak());
            var end = Math.Min(names.Count, index + MaximumItemsPerLine);
            for (var item = index; item < end; item++)
            {
                if (item > index) _text.Inlines.Add(new Run("、") { Foreground = _muted });
                _text.Inlines.Add(new Run(names[item]) { Foreground = _foreground });
                _text.Inlines.Add(new Run(" ") { Foreground = _muted });
                _text.Inlines.Add(new Run(states[item]) { Foreground = StatusBrush(states[item]) });
            }
        }
    }

    private Brush StatusBrush(string state) => state switch
    {
        "正常" => _success,
        "已掉" => _danger,
        _ => _muted,
    };

    private void ApplyTheme()
    {
        var light = IsLightTheme();
        var background = new SolidColorBrush(light ? Color.FromRgb(0xF9, 0xF9, 0xF9) : Color.FromRgb(0x2B, 0x2B, 0x2B));
        var border = new SolidColorBrush(light ? Color.FromRgb(0xE5, 0xE5, 0xE5) : Color.FromRgb(0x3D, 0x3D, 0x3D));
        var foreground = new SolidColorBrush(light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xF2, 0xF2, 0xF2));
        var muted = new SolidColorBrush(light ? Color.FromRgb(0x6B, 0x6B, 0x6B) : Color.FromRgb(0xA8, 0xA8, 0xA8));
        var success = new SolidColorBrush(light ? Color.FromRgb(0x1E, 0x7A, 0x3C) : Color.FromRgb(0x6C, 0xD1, 0x8A));
        var danger = new SolidColorBrush(light ? Color.FromRgb(0xB8, 0x32, 0x2A) : Color.FromRgb(0xFF, 0x8A, 0x80));
        foreach (var brush in new[] { background, border, foreground, muted, success, danger }) brush.Freeze();
        _surface.Background = background;
        _surface.BorderBrush = border;
        _foreground = foreground;
        _muted = muted;
        _success = success;
        _danger = danger;
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
