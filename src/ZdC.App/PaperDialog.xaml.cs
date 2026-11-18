using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ZdC.App;

// ZdC 自有说明与提示统一在这一个纸纹窗口中呈现。
public partial class PaperDialog : Window
{
    public PaperDialog(string title, string message, bool isError = false, bool confirmation = false)
    {
        InitializeComponent();
        // 同一块石板的正面与断面；错误仅切换材质和字色，不改提示文字。
        StoneFace.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isError ? "#252425" : "#9CA49B"));
        StoneMass.Fill = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(isError ? "#494447" : "#858F86"),
            (Color)ColorConverter.ConvertFromString(isError ? "#151416" : "#626D64"), 55);
        var lettering = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(isError ? "#C04F4B" : "#242126"),
            (Color)ColorConverter.ConvertFromString(isError ? "#E27A68" : "#514B4B"), 90);
        Heading.Foreground = Body.Foreground = CloseMark.Stroke = lettering;
        Divider.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isError ? "#514044" : "#D2CCC3"));
        foreach (var text in new[] { Heading, Body })
            text.Effect = new DropShadowEffect { Color = isError ? Color.FromRgb(8, 7, 9) : Color.FromRgb(79, 86, 79),
                ShadowDepth = 0.3, Direction = 90, BlurRadius = 0.35, Opacity = 0.22 };
        MossGrain.Visibility = isError ? Visibility.Collapsed : Visibility.Visible;
        Title = Heading.Text = title;
        Body.Text = message;
        if (title == "使用说明")
        {
            Heading.Visibility = Visibility.Collapsed;
            Divider.Visibility = Visibility.Collapsed;
            Body.FontWeight = FontWeights.SemiBold;
            Body.FontSize = 21;
            Body.LineHeight = 39;
        }
        if (confirmation)
        {
            Heading.Visibility = Divider.Visibility = MossGrain.Visibility = PaperGrain.Visibility = Visibility.Collapsed;
            MarbleGrain.Visibility = ConfirmationActions.Visibility = Visibility.Visible;
            StoneFace.Background = new SolidColorBrush(Color.FromRgb(238, 239, 234));
            StoneMass.Fill = new SolidColorBrush(Color.FromRgb(172, 178, 169));
        }
        Opacity = SystemParameters.ClientAreaAnimation ? 0 : 1;
        ContentRendered += (_, _) =>
        {
            if (SystemParameters.ClientAreaAnimation)
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        };
    }

    public static void ShowMessage(Window? owner, string title, string message, bool isError = false)
    {
        var dialog = new PaperDialog(title, message, isError);
        if (owner is not null) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
    }

    private void StoneFace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var w = e.NewSize.Width;
        var h = e.NewSize.Height;
        if (w <= 32 || h <= 32) return;
        Point[] rim =
        [
            new(4, 7), new(w * .13, 2), new(w * .34, 8),
            new(w * .39, 5), new(w * .72, 1), new(w * .91, 4), new(w - 7, 2),
            new(w - 1, 11), new(w - 5, h * .22), new(w - 11, h * .37),
            new(w - 7, h * .405), new(w - 3, h * .67), new(w - 6, h * .87),
            new(w - 2, h - 13), new(w - 12, h - 2),
            new(w * .81, h - 5), new(w * .64, h - 11), new(w * .59, h - 7),
            new(w * .31, h - 3), new(w * .17, h - 6), new(9, h - 1),
            new(2, h - 11), new(6, h * .76), new(12, h * .63),
            new(8, h * .58), new(2, h * .29), new(4, h * .14)
        ];
        static StreamGeometry Polygon(IEnumerable<Point> points)
        {
            var vertices = points.ToArray();
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(vertices[0], true, true);
                path.PolyLineTo(vertices.Skip(1).ToArray(), true, false);
            }
            geometry.Freeze();
            return geometry;
        }
        StoneFace.Clip = Polygon(rim);
        // 一个实体外轮廓：前上缘→连续斜接→右/下断面后缘→连续斜接→前左缘。
        var solid = rim.Take(7)
            .Concat(rim.Skip(6).Take(15).Select(point => new Point(point.X + 4, point.Y + 5)))
            .Concat(rim.Skip(20));
        var mass = Polygon(solid);
        StoneMass.Data = mass;

    }
    public static bool Confirm(Window owner, string message, bool canConfirm = true)
    {
        var dialog = new PaperDialog("强制停止", message, confirmation: true) { Owner = owner };
        dialog.ConfirmButton.IsEnabled = canConfirm;
        return dialog.ShowDialog() == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Dialog_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.ScrollBar) return;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        if (e.LeftButton == MouseButtonState.Pressed) { DragMove(); e.Handled = true; }
    }
}