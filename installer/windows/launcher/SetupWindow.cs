using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;
using ShapePath = System.Windows.Shapes.Path;
[assembly:AssemblyTitle("ZdC")]
[assembly:AssemblyProduct("ZdC")]

partial class SetupWindow : Window
{
    [STAThread] public static int Main()
    {
        var app=new Application();
        var window=new SetupWindow();
        app.Run(window);
        return window.exitCode;
    }

    private readonly Border StoneFace=new Border {Margin=new Thickness(0,0,4,5)};
    private readonly ShapePath StoneMass=new ShapePath(), StoneGrain=new ShapePath();
    private readonly StackPanel body=new StackPanel {Margin=new Thickness(40,25,40,27)};
    private readonly TextBox folder=new TextBox();
    private readonly CheckBox desktop=new CheckBox();
    private readonly CheckBox tray=new CheckBox();
    private Installed existing;
    private bool busy, updating, trayInitialized;
    private TextBlock status;
    private ProgressBar progress;
    private StackPanel buttons;
    private Button browseButton;
    private CancellationTokenSource cancellation;
    private readonly DockPanel locationLine=new DockPanel {Margin=new Thickness(0,5,0,13)};
    private int exitCode=1602;

    private static Stream Asset(string key) => Assembly.GetExecutingAssembly().GetManifestResourceStream(key);
    private static BitmapImage Picture(string key)
    {
        using(var input=Asset(key)) {
            var image=new BitmapImage();
            image.BeginInit(); image.CacheOption=BitmapCacheOption.OnLoad; image.StreamSource=input; image.EndInit();
            image.Freeze(); return image;
        }
    }
    private static ImageBrush Grain(ImageSource source,double size) => new ImageBrush(source) {
        Viewbox=new Rect(.23,.05,.08,.08), ViewboxUnits=BrushMappingMode.RelativeToBoundingBox,
        Viewport=new Rect(0,0,size,size), ViewportUnits=BrushMappingMode.Absolute, TileMode=TileMode.Tile };

    public SetupWindow()
    {
        Title="安装 ZdC"; Width=570; Height=392; MinHeight=372; MinWidth=500;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;
        WindowStyle=WindowStyle.None; AllowsTransparency=true; Background=Brushes.Transparent;
        ResizeMode=ResizeMode.NoResize; UseLayoutRounding=true; FontFamily=new FontFamily("Palatino Linotype, KaiTi"); FontSize=17;
        using(var stream=Asset("theme.xaml")) Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
        using(var stream=Asset("mass.xaml")) StoneMass.Fill=(Brush)XamlReader.Load(stream);
        Foreground=(Brush)FindResource("Ink"); StoneFace.Background=(Brush)FindResource("Canvas");
        var slate=Picture("ZdC.png"); StoneGrain.Fill=Grain(slate,80); StoneGrain.Opacity=.22;
        var root=new Grid(); root.Children.Add(StoneMass); root.Children.Add(StoneGrain); root.Children.Add(StoneFace);
        var face=new Grid(); StoneFace.Child=face;
        face.Children.Add(new Rectangle { Fill=Grain(slate,110),Opacity=.30,IsHitTestVisible=false });
        face.Children.Add(body); Content=root; StoneFace.SizeChanged+=StoneFace_SizeChanged;
        Closing+=(s,e)=>{ if(busy)e.Cancel=true; };
        KeyDown+=(s,e)=>{if(e.Key==Key.Escape && !busy)Close();};
        BuildPage(); Loaded+=(s,e)=>LoadOptions();
    }

    private TextBlock Label(string caption,double size=17)
    {
        return new TextBlock {Text=caption,FontSize=size,TextWrapping=TextWrapping.Wrap,
            VerticalAlignment=VerticalAlignment.Center,Foreground=(Brush)FindResource("Ink")};
    }

    private Button ActionButton(string caption,Action click,bool primary=false)
    {
        var button=new Button {Content=Label(caption),MinWidth=78,Height=35,Padding=new Thickness(13,2,13,2),
            Margin=new Thickness(8,0,0,0),IsDefault=primary,Cursor=Cursors.Hand,FocusVisualStyle=(Style)FindResource("KeyboardOnlyFocus")};
        button.Template=StoneButtonTemplate();
        button.Click+=(s,e)=>click();
        AutomationProperties.SetName(button,caption);
        return button;
    }

    private ControlTemplate StoneButtonTemplate()
    {
        var border=new FrameworkElementFactory(typeof(Border));
        border.Name="surface"; border.SetValue(Border.CornerRadiusProperty,new CornerRadius(2));
        border.SetValue(Border.BorderBrushProperty,(Brush)FindResource("Line")); border.SetValue(Border.BorderThicknessProperty,new Thickness(1));
        border.SetValue(Border.BackgroundProperty,(Brush)FindResource("InsetSurface"));
        var content=new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty,HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty,VerticalAlignment.Center); border.AppendChild(content);
        var result=new ControlTemplate(typeof(Button)) {VisualTree=border};
        var hover=new Trigger {Property=UIElement.IsMouseOverProperty,Value=true};
        hover.Setters.Add(new Setter(Border.BorderBrushProperty,FindResource("FocusBorder"),"surface")); result.Triggers.Add(hover);
        return result;
    }

    private void BuildPage()
    {
        var header=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,10)};
        header.Children.Add(new Image {Source=Picture("ZdCMark.png"),Width=80,Height=54,Margin=new Thickness(-7,0,12,0)});
        var title=Label("安装 ZdC",27); title.Foreground=(Brush)FindResource("StoneGlyph"); header.Children.Add(title);
        header.MouseLeftButtonDown+=(s,e)=>{if(e.ButtonState==MouseButtonState.Pressed)DragMove();};
        body.Children.Add(header);
        folder.Text=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"ZdC");
        desktop.IsChecked=true;
        body.Children.Add(Label("安装位置"));
        browseButton=ActionButton("浏览…",Browse);DockPanel.SetDock(browseButton,Dock.Right);locationLine.Children.Add(browseButton);
        folder.Height=35;folder.Padding=new Thickness(6,2,6,2);folder.VerticalContentAlignment=VerticalAlignment.Center;
        folder.Background=(Brush)FindResource("InsetSurface");folder.BorderBrush=(Brush)FindResource("Line");
        AutomationProperties.SetName(folder,"安装位置");locationLine.Children.Add(folder);body.Children.Add(locationLine);
        desktop.Content=Label("创建桌面快捷方式");desktop.VerticalContentAlignment=VerticalAlignment.Center;
        desktop.FocusVisualStyle=(Style)FindResource("KeyboardOnlyFocus");
        AutomationProperties.SetName(desktop,"创建桌面快捷方式");body.Children.Add(desktop);
        // 默认勾选；更新安装时在 RefreshRegistration 里改为读取用户当前偏好。
        tray.IsChecked=true;tray.Content=Label("关闭窗口后最小化到托盘");tray.VerticalContentAlignment=VerticalAlignment.Center;
        tray.Margin=new Thickness(0,6,0,0);tray.FocusVisualStyle=(Style)FindResource("KeyboardOnlyFocus");
        AutomationProperties.SetName(tray,"关闭窗口后最小化到托盘");body.Children.Add(tray);
        status=Label("");status.Margin=new Thickness(0,7,0,3);status.MinHeight=24;body.Children.Add(status);
        progress=new ProgressBar {Minimum=0,Maximum=100,Height=8,Visibility=Visibility.Hidden,
            Background=(Brush)FindResource("InsetSurface"),Foreground=(Brush)FindResource("StoneGlyph")};
        var track=new FrameworkElementFactory(typeof(Border));track.Name="PART_Track";
        track.SetValue(Border.BackgroundProperty,(Brush)FindResource("InsetSurface"));track.SetValue(Border.BorderThicknessProperty,new Thickness(1));
        track.SetValue(Border.BorderBrushProperty,(Brush)FindResource("Line"));
        var fill=new FrameworkElementFactory(typeof(Border));fill.Name="PART_Indicator";
        fill.SetValue(Border.HorizontalAlignmentProperty,HorizontalAlignment.Left);fill.SetValue(Border.BackgroundProperty,(Brush)FindResource("StoneGlyph"));
        track.AppendChild(fill);progress.Template=new ControlTemplate(typeof(ProgressBar)){VisualTree=track};body.Children.Add(progress);
        buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
        body.Children.Add(buttons);
    }
    private void Buttons(params Button[] controls)
    {
        buttons.Children.Clear();foreach(var child in controls)buttons.Children.Add(child);
    }
    private void Editable(bool enabled)
    {
        folder.IsReadOnly=updating || !enabled;browseButton.IsEnabled=enabled && !updating;desktop.IsEnabled=enabled;tray.IsEnabled=enabled;
    }
    private bool RefreshRegistration(Action retry)
    {
        try {
            var registered=InstallEngine.Find();
            // An interrupted upgrade can temporarily lose its MSI registration. Keep the update pending.
            if(existing!=null && registered==null){Editable(false);Fault(1638,retry);return false;}
            existing=registered;updating=existing!=null;
            if(updating && Version.Parse(existing.Version)>Version.Parse(Identity.Version)){Editable(false);Fault(1638,LoadOptions);return false;}
            if(updating) folder.Text=existing.Directory.TrimEnd('\\');
            Editable(true);return true;
        } catch(Exception e){Editable(false);Fault(ErrorNumber(e),LoadOptions);return false;}
    }
    private void LoadOptions()
    {
        if(!RefreshRegistration(LoadOptions))return;
        // 只在首次成功读取注册状态时用已有偏好初始化；之后保留用户在窗口里的选择。
        if(!trayInitialized){ if(updating) tray.IsChecked=TrayChoice.Enabled; trayInitialized=true; }
        status.Text="";progress.Visibility=Visibility.Hidden;
        Buttons(ActionButton("安装",Begin,true),ActionButton("取消",Close));
    }
    private void Browse()
    {
        using(var dialog=new Forms.FolderBrowserDialog {SelectedPath=folder.Text,ShowNewFolderButton=true})
        {
            if(dialog.ShowDialog(new Win32Owner(new WindowInteropHelper(this).Handle))==Forms.DialogResult.OK)
                folder.Text=dialog.SelectedPath;
        }
    }
    private sealed class Win32Owner : Forms.IWin32Window
    {
        public IntPtr Handle {get;}
        public Win32Owner(IntPtr handle){Handle=handle;}
    }
    private void Problem(string message)
    {
        status.Text=message;progress.Visibility=Visibility.Hidden;Editable(true);
        Buttons(ActionButton("重试",Begin,true),ActionButton("关闭",Close));
    }
    private static int ErrorNumber(Exception e) => e is Win32Exception w ? w.NativeErrorCode : e.HResult & 0xffff;
    private void Fault(int code,Action retry,string logPath=null)
    {
        exitCode=code;status.Text=(updating?"更新失败":"安装失败")+"　错误代码 "+code;
        if (logPath != null && File.Exists(logPath)) {
            status.Text += Environment.NewLine + logPath;
            SizeToContent = SizeToContent.Height;
        }
        progress.Visibility=Visibility.Hidden;Buttons(ActionButton("重试",retry,true),ActionButton("关闭",Close));
    }
    private async void Begin()
    {
        if(busy || !RefreshRegistration(Begin))return;
        string path;
        try {
            if(InstallEngine.AppRunning()){Problem("请先退出 ZdC，再继续。");return;}
            if(!InstallEngine.RuntimeAvailable()){Problem("需要 .NET 9 桌面运行时（x64）。");return;}
            path=InstallEngine.CheckDirectory(folder.Text,existing);
        } catch(Win32Exception x) when(x.NativeErrorCode==112){Problem("磁盘空间不足，请选择其他位置。");return;}
          catch(Exception x) when(x is ArgumentException || x is IOException || x is UnauthorizedAccessException || x is NotSupportedException) {
              Problem("请选择有效的安装位置。");return;
          } catch(Exception x){Fault(ErrorNumber(x),Begin);return;}
        bool shortcut=desktop.IsChecked==true;
        string logPath=null;
        SizeToContent=SizeToContent.Manual;
        var owner=new WindowInteropHelper(this).Handle;busy=true;Editable(false);
        cancellation=new CancellationTokenSource();
        var cancel=ActionButton("取消",RequestCancellation);Buttons(cancel);
        status.Text=updating?"正在更新 ZdC…":"正在安装 ZdC…";progress.Value=0;progress.Visibility=Visibility.Visible;
        try {
            var reporter=new Progress<int>(n=>{if(busy)progress.Value=n;});
            uint result=await Task.Run(()=>InstallEngine.Apply(existing,path,shortcut,owner,reporter,cancellation.Token,out logPath));
            if(result==0 || result==3010) {
                exitCode=(int)result;progress.Value=100;status.Text=updating?"更新完成":"安装完成";
                try { TrayChoice.Save(tray.IsChecked==true); }
                catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException) { status.Text += "（托盘设置保存失败）"; }
                Buttons(ActionButton("完成",Close,true));
            } else if(result==1602 && cancellation.IsCancellationRequested){exitCode=1602;busy=false;Close();}
            else if(result==112 || result==39){exitCode=(int)result;Problem("磁盘空间不足，请选择其他位置。");}
            else {Editable(true);Fault((int)result,Begin,logPath);}
        } catch(InstallEngine.AppInUseException){Problem("请先退出 ZdC，再继续。");}
          catch(Exception x){Editable(true);Fault(ErrorNumber(x),Begin,logPath);}
        finally {busy=false;cancellation.Dispose();cancellation=null;}
    }
    private void RequestCancellation()
    {
        if(cancellation==null || cancellation.IsCancellationRequested)return;
        cancellation.Cancel();
        foreach(Button button in buttons.Children)button.IsEnabled=false;
    }
}

/// <summary>安装器写用户偏好文件；应用启动时按同一路径读取，无需额外协议。</summary>
internal static class TrayChoice
{
    private static readonly string Marker=System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ZdC","close-to-tray");

    internal static bool Enabled => File.Exists(Marker);

    internal static void Save(bool enabled)
    {
        if(enabled)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Marker)!);
            using var marker=new FileStream(Marker,FileMode.OpenOrCreate,FileAccess.Write,FileShare.Read);
        }
        else
        {
            try { File.Delete(Marker); }
            catch(DirectoryNotFoundException) { }
        }
    }
}
