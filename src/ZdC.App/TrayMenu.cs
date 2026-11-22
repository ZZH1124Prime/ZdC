using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace ZdC.App;

/// <summary>通知区域右键菜单：自绘 Win11 风格，避免 WinForms 菜单的旧样式。</summary>
internal static class TrayMenu
{
    internal static ContextMenu Create()
    {
        var light = TrayTheme.IsLight();
        var background = light ? "#F9F9F9" : "#2B2B2B";
        var border = light ? "#E7E7E7" : "#3D3D3D";
        var ink = light ? "#1A1A1A" : "#F2F2F2";
        var hover = light ? "#EFEFEF" : "#3A3A3A";
        var pressed = light ? "#E5E5E5" : "#434343";
        var xaml = $$"""
            <ContextMenu xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         HasDropShadow="True">
              <ContextMenu.Resources>
                <Style TargetType="MenuItem">
                  <Setter Property="Foreground" Value="{{ink}}" />
                  <Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI" />
                  <Setter Property="FontSize" Value="13" />
                  <Setter Property="Template">
                    <Setter.Value>
                      <ControlTemplate TargetType="MenuItem">
                        <Border x:Name="Item" Margin="2,1" Padding="10,7" CornerRadius="4" Background="Transparent">
                          <ContentPresenter ContentSource="Header" RecognizesAccessKey="True" />
                        </Border>
                        <ControlTemplate.Triggers>
                          <Trigger Property="IsHighlighted" Value="True">
                            <Setter TargetName="Item" Property="Background" Value="{{hover}}" />
                          </Trigger>
                          <Trigger Property="IsPressed" Value="True">
                            <Setter TargetName="Item" Property="Background" Value="{{pressed}}" />
                          </Trigger>
                          <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.45" />
                          </Trigger>
                        </ControlTemplate.Triggers>
                      </ControlTemplate>
                    </Setter.Value>
                  </Setter>
                </Style>
                <Style TargetType="Separator">
                  <Setter Property="Template">
                    <Setter.Value>
                      <ControlTemplate TargetType="Separator">
                        <Border Height="1" Margin="8,4" Background="{{border}}" />
                      </ControlTemplate>
                    </Setter.Value>
                  </Setter>
                </Style>
              </ContextMenu.Resources>
              <ContextMenu.Template>
                <ControlTemplate TargetType="ContextMenu">
                  <Border Margin="8" Padding="4" CornerRadius="8" Background="{{background}}"
                          BorderBrush="{{border}}" BorderThickness="1" SnapsToDevicePixels="True">
                    <Border.Effect>
                      <DropShadowEffect Color="#000000" BlurRadius="12" ShadowDepth="2" Direction="270" Opacity="0.18" />
                    </Border.Effect>
                    <ScrollViewer VerticalScrollBarVisibility="Auto">
                      <ItemsPresenter />
                    </ScrollViewer>
                  </Border>
                </ControlTemplate>
              </ContextMenu.Template>
              <MenuItem Header="打开主界面" />
              <Separator />
              <MenuItem Header="退出" />
            </ContextMenu>
            """;
        var menu = (ContextMenu)XamlReader.Parse(xaml);
        menu.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        return menu;
    }

    /// <summary>右键菜单按光标位置弹出，并避开屏幕边缘。</summary>
    internal static void Place(ContextMenu menu, Window owner, System.Drawing.Point cursor)
    {
        var dpi = VisualTreeHelper.GetDpi(owner);
        var screen = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        var x = (double)cursor.X;
        var y = (double)cursor.Y;
        if (menu.ActualWidth > 0 && menu.ActualHeight > 0)
        {
            var width = menu.ActualWidth * dpi.DpiScaleX;
            var height = menu.ActualHeight * dpi.DpiScaleY;
            if (x + width > screen.Right - 4) x = Math.Max(screen.Left + 4, cursor.X - width);
            if (y + height > screen.Bottom - 4) y = Math.Max(screen.Top + 4, cursor.Y - height);
        }
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = x / dpi.DpiScaleX;
        menu.VerticalOffset = y / dpi.DpiScaleY;
    }
}

/// <summary>安装/提示/菜单共用的系统浅色判断。</summary>
internal static class TrayTheme
{
    internal static bool IsLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is not int theme || theme != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
