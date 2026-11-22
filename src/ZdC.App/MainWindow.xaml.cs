using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ZdC.Core;

namespace ZdC.App;

public partial class MainWindow : Window
{
    private readonly RegisteredApplicationManager _applications = new(
        Path.Combine(AppContext.BaseDirectory, "ZdC.SocketController.exe"));
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private RegisteredApplication[] _entries = [];
    private bool _busy = true, _loaded, _shutdownCompleted, _refreshing;
    private bool _displayEnabled;
    private readonly HashSet<string> _applicationOperations = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        TrayOption.IsChecked = TrayPreference.Enabled;
        Loaded += OnInitialLoad;
        Application.Current.Exit += Application_Exit;
        Closing += OnClosing;
        _statusTimer.Tick += async (_, _) => { if (IsVisible) await RefreshStatesAsync(); };
    }

    // 自绘标题栏仍使用 Windows 原生窗口操作，业务关闭流程继续走 OnClosing。
    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            if (WindowState == WindowState.Maximized)
            {
                var pointer = e.GetPosition(this);
                var screen = PointToScreen(pointer);
                var desktop = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice.Transform(screen) ?? screen;
                var ratio = Math.Clamp(pointer.X / ActualWidth, 0, 1);
                var width = RestoreBounds.Width;
                WindowState = WindowState.Normal;
                Left = desktop.X - width * ratio;
                Top = desktop.Y - Math.Min(pointer.Y, 40);
            }
            DragMove();
        }
    }

    // 较大的自然断裂段与少量小崩口共用一套轮廓，避免规则锯齿或前后错层。
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
        StoneGrain.Data = mass;
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private async void OnInitialLoad(object sender, RoutedEventArgs e)
    {
        Loaded -= OnInitialLoad;
        await LoadAsync();
    }
    private void Help_Click(object sender, RoutedEventArgs e) => PaperDialog.ShowMessage(this, "使用说明",
        "拖入应用或快捷方式，也可点右上角 ＋ 添加。\n\n" +
        "点未运行的应用图标，从 ZdC 启动；已运行时再点图标，单独切换此应用直连。已有普通实例时，先正常退出该应用。\n\n" +
        "顶部绿三角批量开启，红方块批量暂停；各应用独立完成，单项失败不影响其他项。停止或退出 ZdC 后，新连接跟随系统；已有连接需重连。\n\n" +
        "右键应用可替换入口或移除记录。移除记录保留原程序和快捷方式。\n\n" +
        "延迟为近10秒内有效 TCP 连接样本的 RTT 中位数，无有效样本时显示 ---ms。经过本地代理或 TUN 时，数值对应当前 TCP 连接端点。\n\n" +
        "目前仅接管从 ZdC 启动的应用。部分异步、UDP 和 IPv6 路径仍有覆盖限制。");
    private async Task LoadAsync()
    {
        try
        {
            _entries = (await _applications.LoadAsync()).ToArray();
            _loaded = true;
            await RefreshAsync();
            StatusText.Text = "";
            _statusTimer.Start();
        }
        catch (Exception ex) { StatusText.Text = $"读取名单失败：{ex.Message}"; }
        finally { SetBusy(false); }
    }

    private async Task RefreshAsync(string? selectPath = null)
    {
        selectPath ??= (AppList.SelectedItem as ApplicationRow)?.Path;
        var rows = new List<ApplicationRow>();
        foreach (var entry in _entries)
        {
            rows.Add(new ApplicationRow(entry, await GetIconAsync(entry)));
        }
        for (var index = 0; index < rows.Count - 1; index++) rows[index].SeparatorVisibility = Visibility.Visible;
        AppList.ItemsSource = rows;
        AppList.SelectedItem = rows.FirstOrDefault(x => string.Equals(x.Path, selectPath, StringComparison.OrdinalIgnoreCase));
        CountText.Text = $"{rows.Count} 个应用";
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        await RefreshStatesAsync(force: true);
        UpdateSelection();
    }

    private async Task<ImageSource?> GetIconAsync(RegisteredApplication entry)
    {
        var key = $"{entry.IconPath}|{entry.IconIndex}|{entry.Path}";
        if (_icons.TryGetValue(key, out var icon)) return icon;
        icon = await Task.Run(() => ApplicationIcon.Read(entry));
        _icons[key] = icon;
        return icon;
    }

    private async Task RefreshStatesAsync(bool force = false)
    {
        if (!_loaded || _shutdownCompleted || _refreshing || (_busy && !force)) return;
        _refreshing = true;
        try
        {
            var running = 0;
            var states = new List<ApplicationSessionState>();
            var errors = new List<string>();
            var uncertain = false;
            await Task.WhenAll(AppList.Items.OfType<ApplicationRow>().Select(async row =>
            {
                try
                {
                    var state = await _applications.GetSessionStateAsync(row.Path);
                    states.Add(state);
                    row.SetState(File.Exists(row.Path), state.IsRunning, state.RoutingEnabled);
                    row.SetTelemetry(state);
                    if (state.IsRunning && state.RoutingEnabled) running++;
                }
                catch (Exception ex)
                {
                    uncertain = true;
                    if (!row.HasOperationFailure) errors.Add($"{row.Name}：{ex.Message}");
                    row.FailOperation(ex.Message, recoverOnObservation: true);
                }
            }));
            ApplyGlobalState(states, uncertain);
            if (errors.Count > 0) PaperDialog.ShowMessage(this, "应用状态读取失败", string.Join("\n\n", errors) + "\n\n其他应用继续运行。", isError: true);
            SessionText.Text = running > 0 ? $"{running} 个应用已启用直连" : (_displayEnabled ? "准备就绪" : "新连接跟随系统");
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { ApplyGlobalState([], uncertain: true); StatusText.Text = $"状态读取失败：{ex.Message}"; }
        finally { _refreshing = false; }
    }

    private async Task RunAsync(Func<Task> action, string success, string? selectPath = null)
    {
        if (_busy || !_loaded || _applicationOperations.Count > 0) return;
        SetBusy(true);
        try
        {
            await action();
            _entries = (await _applications.ListAsync()).ToArray();
            await RefreshAsync(selectPath);
            StatusText.Text = success;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            // 批次导入可能已有成功项；只重新显示已保存快照。
            try { _entries = (await _applications.ListAsync()).ToArray(); await RefreshAsync(selectPath); }
            catch { }
            if (ex is RegistryChangedException) _loaded = false;
        }
        finally { SetBusy(false); }
    }

    private static string[] PickEntries(string title, bool multiple)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = "应用与快捷方式 (*.lnk;*.exe)|*.lnk;*.exe",
            CheckFileExists = true, Multiselect = multiple, DereferenceLinks = false };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    private async Task ImportAsync(string[] paths, ApplicationRow? replacement = null)
    {
        await RunAsync(async () =>
        {
            if (replacement is not null)
            {
                if (paths.Length != 1) throw new InvalidOperationException("替换应用时，请拖入一个新的快捷方式或程序。");
                await _applications.ReplaceAsync(replacement.Path, paths[0]);
                _icons.Clear();
            }
            else foreach (var path in paths) await _applications.ImportAsync(path);
        }, replacement is null ? "已加入应用。点击图标即可启动。" : "入口已替换，点击图标重新启动。");
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_loaded || _applicationOperations.Count > 0) return;
        var paths = PickEntries("添加应用或快捷方式", true);
        if (paths.Length > 0) await ImportAsync(paths);
    }
    private void Drag_Files(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && _applicationOperations.Count == 0 && _loaded && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void Drop_Files(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_busy || _applicationOperations.Count > 0 || !_loaded || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var target = e.OriginalSource as DependencyObject;
        ApplicationRow? replacement = null;
        while (target is not null)
        {
            if (target is FrameworkElement { DataContext: ApplicationRow row })
            { if (!File.Exists(row.Path)) replacement = row; break; }
            target = target is Visual ? VisualTreeHelper.GetParent(target) : LogicalTreeHelper.GetParent(target);
        }
        await ImportAsync(paths, replacement);
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ApplicationRow row } || _busy || !_loaded || !_applicationOperations.Add(row.Path)) return;
        AppList.SelectedItem = row;
        row.BeginOperation();
        SetBusy(_busy);
        var operationFailed = false;
        var attemptedLaunch = false;
        string? launchedPath = null;
        var operationPath = row.Path;
        try
        {
            var state = await _applications.GetSessionStateAsync(row.Path);
            if (state.IsRunning)
            {
                operationFailed = true;
                await _applications.SetEnabledAsync(row.Path, !state.RoutingEnabled);
                operationFailed = false;
                row.EndOperation();
                state = await _applications.GetSessionStateAsync(row.Path);
                row.SetState(true, state.IsRunning, state.RoutingEnabled);
                row.SetTelemetry(state);
                return;
            }
            var blockers = await _applications.GetBlockingProcessesAsync(row.Path);
            if (blockers.Count != 0)
            {
                await ConfirmForceStopAsync(row.Path, blockers);
                return;
            }
            attemptedLaunch = true;
            operationFailed = true;
            launchedPath = (await _applications.LaunchAsync(row.Path, routingEnabled: true)).RegisteredPath;
            operationFailed = false;
            if (!string.Equals(launchedPath, row.Path, StringComparison.OrdinalIgnoreCase)) return;
            row.EndOperation();
            state = await _applications.GetSessionStateAsync(row.Path);
            row.SetState(true, state.IsRunning, state.RoutingEnabled);
            row.SetTelemetry(state);
        }
        catch (Exception ex)
        {
            row.FailOperation(ex.Message, recoverOnObservation: !operationFailed);
            PaperDialog.ShowMessage(this, "应用操作失败", $"{row.Name}\n\n{ex.Message}\n\n其他应用继续运行。", isError: true);
        }
        finally
        {
            try
            {
                if (attemptedLaunch && !string.Equals(launchedPath, operationPath, StringComparison.OrdinalIgnoreCase))
                {
                    // Automatic replacement persists in the same slot, including when the next launch is blocked.
                    // Keep the other rows and their in-flight operations intact.
                    var current = await _applications.ListAsync();
                    var index = Array.FindIndex(_entries, e => string.Equals(e.Path, operationPath, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0 && current.Count == _entries.Length &&
                        !string.Equals(current[index].Path, operationPath, StringComparison.OrdinalIgnoreCase))
                    {
                        var updated = current[index];
                        var icon = await GetIconAsync(updated);
                        _entries[index] = updated;
                        row.UpdateEntry(updated, icon);
                    }
                }
            }
            catch (Exception ex) { StatusText.Text = $"状态读取失败：{ex.Message}"; }
            finally
            {
                row.EndOperation();
                _applicationOperations.Remove(operationPath);
                SetBusy(_busy);
            }
            await RefreshStatesAsync(force: true);
        }
    }
    // 任一确认开启即可暂停全部；读取不完整时保留上次状态，不显示第三种图形。
    private void ApplyGlobalState(IReadOnlyList<ApplicationSessionState> states, bool uncertain = false)
    {
        if (!uncertain)
            _displayEnabled = states.Any(s => s.IsRunning && s.RoutingEnabled);
        GlobalToggle.IsChecked = _displayEnabled;
        GlobalToggle.ToolTip = _displayEnabled ? "暂停直连" : "开启直连";
        System.Windows.Automation.AutomationProperties.SetName(GlobalToggle,
            _displayEnabled ? "暂停直连" : "开启直连");
    }
    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_loaded || _refreshing || _applicationOperations.Count > 0) { GlobalToggle.IsChecked = _displayEnabled; return; }
        var enabled = !_displayEnabled;

        SetBusy(true);
        var rows = AppList.Items.OfType<ApplicationRow>().ToArray();
        // 每个应用独立执行、独立结束；某项失败不撤销其他项已完成的切换。
        var results = await Task.WhenAll(rows.Select(async row =>
        {
            var commandFailed = false;
            try
            {
                var state = await _applications.GetSessionStateAsync(row.Path);
                if (!state.IsRunning) return (string?)null;
                row.BeginOperation();
                commandFailed = true;
                await _applications.SetEnabledAsync(row.Path, enabled);
                commandFailed = false;
                row.EndOperation();
                state = await _applications.GetSessionStateAsync(row.Path);
                row.SetState(File.Exists(row.Path), state.IsRunning, state.RoutingEnabled);
                row.SetTelemetry(state);
                return null;
            }
            catch (Exception ex)
            {
                row.FailOperation(ex.Message, recoverOnObservation: !commandFailed);
                return $"{row.Name}：{ex.Message}";
            }
            finally { row.EndOperation(); }
        }));
        SetBusy(false);
        await RefreshStatesAsync(force: true);
        var failures = results.Where(message => message is not null).ToArray();
        if (failures.Length > 0)
            PaperDialog.ShowMessage(this, "部分应用切换失败", string.Join("\n\n", failures) + "\n\n其他应用已各自完成，不受影响。", isError: true);
    }

    private async void ForceStop_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_loaded || _applicationOperations.Count > 0 ||
            AppList.SelectedItem is not ApplicationRow row) return;
        SetBusy(true);
        try
        {
            var snapshot = await _applications.GetBlockingProcessesAsync(row.Path);
            if (!await ConfirmForceStopAsync(row.Path, snapshot)) return;
            await RefreshStatesAsync(force: true);
        }
        catch (Exception ex)
        {
            PaperDialog.ShowMessage(this, "应用操作失败",
                $"{row.Name}\n\n{ex.Message}\n\n其他应用继续运行。", isError: true);
        }
        finally { SetBusy(false); }
    }
    private async Task<bool> ConfirmForceStopAsync(string path, IReadOnlyList<ApplicationBlockingProcess> snapshot)
    {
        // Both callers stop only the snapshot shown in this dialog.
        var list = string.Join("\n\n", snapshot.Select(process =>
            $"{process.Name} (PID {process.ProcessId})\n{process.ExecutablePath}"));
        if (!PaperDialog.Confirm(this,
            $"当前阻塞应用开启的进程：\n{list}\n\n是否全部停止", snapshot.Count > 0)) return false;
        await _applications.ForceStopBlockingProcessesAsync(path, snapshot);
        return true;
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not ApplicationRow row) return;
        await RunAsync(async () => { await _applications.RemoveAsync(row.Path); }, "已移除。应用仍可继续使用，新连接跟随系统。");
    }
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || AppList.SelectedItem is not ApplicationRow row) return;
        var paths = PickEntries("替换应用入口", false);
        if (paths.Length > 0) await ImportAsync(paths, row);
    }
    private async void Inspect_Click(object sender, RoutedEventArgs e)
    {
        InspectButton.IsEnabled = false;
        DiagnosticsPanel.IsExpanded = true;
        try
        {
            var report = await Task.Run(() => RoutingPreflight.Inspect(AppContext.BaseDirectory));
            DiagnosticsText.Text = string.Join(Environment.NewLine, report.Adapters.Select(a =>
                $"{a.Name}  #{a.InterfaceIndex}  {string.Join(", ", a.Addresses)}")) +
                "\n\n从 ZdC 点击启动的应用由当前会话控制。关闭 ZdC 后，新连接恢复系统默认；已有连接需重新建立。";
        }
        catch (Exception ex) { DiagnosticsText.Text = ex.Message; }
        finally { InspectButton.IsEnabled = true; }
    }
    private bool _contextFromIcon;
    private void AppList_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _contextFromIcon = false;
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source != AppList)
        {
            if (source is Button { DataContext: ApplicationRow row })
            {
                AppList.SelectedItem = row;
                _contextFromIcon = true;
                break;
            }
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
    }
    private void AppList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!_contextFromIcon) e.Handled = true;
        _contextFromIcon = false;
    }
    private void Icon_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    { if (sender is FrameworkElement { DataContext: ApplicationRow row }) row.SetIconHover(true); }
    private void Icon_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    { if (sender is FrameworkElement { DataContext: ApplicationRow row }) row.SetIconHover(false); }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateSelection();
    private void SetBusy(bool busy)
    {
        _busy = busy;
        AddButton.IsEnabled = GlobalToggle.IsEnabled = _loaded && !busy && _applicationOperations.Count == 0;
        AppList.IsHitTestVisible = !busy;
        UpdateSelection();
    }
    private void UpdateSelection()
    {
        if (UpdateButton is null) return;
        var row = AppList.SelectedItem as ApplicationRow;
        ForceStopButton.IsEnabled = UpdateButton.IsEnabled = RemoveButton.IsEnabled = _loaded && !_busy && _applicationOperations.Count == 0 && row is not null;
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownCompleted) return;
        e.Cancel = true;
        if (_busy || _applicationOperations.Count > 0) { StatusText.Text = "正在完成当前操作，请稍后关闭。"; return; }
        _statusTimer.Stop();
        SetBusy(true);
        try
        {
            await StopSessionsAsync();
            _shutdownCompleted = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        catch (Exception ex) { _shutdownTask = null; StatusText.Text = $"停止接管失败：{ex.Message} 可再次关闭重试。"; _statusTimer.Start(); }
        finally { SetBusy(false); }
    }
}

public sealed class ApplicationRow(RegisteredApplication entry, ImageSource? icon) : INotifyPropertyChanged
{
    public RegisteredApplication Entry { get; private set; } = entry;
    public Visibility SeparatorVisibility { get; set; } = Visibility.Collapsed;
    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public string ActionHint { get; private set; } = "启动应用";
    public ImageSource? Icon { get; private set; } = icon;
    public void UpdateEntry(RegisteredApplication updated, ImageSource? updatedIcon)
    {
        Entry = updated;
        Icon = updatedIcon;
        PropertyChanged?.Invoke(this, new(nameof(Entry)));
        PropertyChanged?.Invoke(this, new(nameof(Name)));
        PropertyChanged?.Invoke(this, new(nameof(Path)));
        PropertyChanged?.Invoke(this, new(nameof(Icon)));
    }
    public bool IsIconHovered { get; private set; }
    public void SetIconHover(bool value)
    {
        IsIconHovered = value;
        PropertyChanged?.Invoke(this, new(nameof(IsIconHovered)));
    }
    public string Status { get; private set; } = "点击图标启动";
    public Brush StatusBrush { get; private set; } = (Brush)Application.Current.FindResource("Muted");
    public string TelemetryText { get; private set; } = "---ms";
    public Brush TelemetryBrush { get; private set; } = (Brush)Application.Current.FindResource("Muted");
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool _operating, _connecting, _completing, _connectionCompleted;
    private int _progressVersion;
    public bool IsProgressCompleting => _completing;
    private string? _operationFailure;
    private bool _recoverOnObservation;
    public bool HasOperationFailure => _operationFailure is not null;
    public Visibility ProgressVisibility => _operating || _connecting || _completing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TelemetryVisibility => ProgressVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    private void NotifyProgress()
    {
        PropertyChanged?.Invoke(this, new(nameof(IsProgressCompleting)));
        PropertyChanged?.Invoke(this, new(nameof(ProgressVisibility)));
        PropertyChanged?.Invoke(this, new(nameof(TelemetryVisibility)));
    }
    public void BeginOperation()
    {
        _operationFailure = null;
        _recoverOnObservation = false;
        _progressVersion++;
        _completing = false;
        _connectionCompleted = false;
        _operating = _connecting = true;
        Status = "正在准备此应用";
        NotifyProgress();
        PropertyChanged?.Invoke(this, new(nameof(Status)));
    }
    public void EndOperation() { _operating = false; NotifyProgress(); }
    public void FailOperation(string message, bool recoverOnObservation = false)
    {
        if (_operationFailure is not null && !_recoverOnObservation && recoverOnObservation) return;
        _recoverOnObservation = recoverOnObservation;
        _operationFailure = message;
        _progressVersion++;
        _operating = _connecting = _completing = false;
        Status = message;
        TelemetryText = "---ms";
        TelemetryBrush = (Brush)Application.Current.FindResource("Muted");
        NotifyProgress();
        PropertyChanged?.Invoke(this, new(nameof(Status)));
        PropertyChanged?.Invoke(this, new(nameof(TelemetryText)));
        PropertyChanged?.Invoke(this, new(nameof(TelemetryBrush)));
    }
    // 连接观测来自核心采样；没有真实耗时样本时不显示毫秒数。
    public void SetTelemetry(ApplicationSessionState state)
    {
        if (_recoverOnObservation && state.ConnectionStatus != ApplicationConnectionStatus.Unavailable) { _operationFailure = null; _recoverOnObservation = false; }
        if (_operationFailure is not null) { FailOperation(_operationFailure, _recoverOnObservation); return; }
        var observed = state.ConnectionStatus is ApplicationConnectionStatus.DirectObserved or ApplicationConnectionStatus.DefaultObserved;
        var routeLabel = state.ConnectionStatus == ApplicationConnectionStatus.DefaultObserved ? "系统默认路由" : "物理接口";
        var hasSample = observed && state.LatencyMs is double value && double.IsFinite(value) && value >= 0;
        var ready = state.ConnectionStatus == ApplicationConnectionStatus.DirectObserved && hasSample;
        if (state.IsRunning && state.RoutingEnabled)
        {
            if (ready) _connectionCompleted = true;
            if (ready && _connecting && !_completing)
            {
                _connecting = false;
                _completing = true;
                _ = FinishProgressAsync(++_progressVersion);
            }
            else if (!ready && !_completing) _connecting = !_connectionCompleted;
        }
        else
        {
            _progressVersion++;
            _connectionCompleted = false;
            _connecting = _completing = false;
        }
        NotifyProgress();
        TelemetryText = hasSample ? $"{state.LatencyMs!.Value:0}ms" : "---ms";
        TelemetryBrush = hasSample ? CreateLatencyBrush(state.LatencyMs!.Value)
            : (Brush)Application.Current.FindResource("Muted");
        if (state.IsExternalProcessRunning)
            Status = "";
        else if (observed)
        {
            Status = state.IsObservationStale
                ? "暂留上次连接结果，正在确认。"
                : $"已观测到 {state.ObservedConnectionCount} 条{routeLabel} TCP 连接。";
            if (hasSample) Status += $"近10秒有效 TCP 样本中位延迟 {state.LatencyMs:0} ms。";
        }
        PropertyChanged?.Invoke(this, new(nameof(TelemetryText)));
        PropertyChanged?.Invoke(this, new(nameof(TelemetryBrush)));
        PropertyChanged?.Invoke(this, new(nameof(Status)));
    }

    private async Task FinishProgressAsync(int version)
    {
        await Task.Delay(420);
        if (_progressVersion != version) return;
        _completing = false;
        NotifyProgress();
    }
    // 阈值区间保持绿、黄、红语义；每个区间内部随真实耗时逐渐加深。
    private static Brush CreateLatencyBrush(double milliseconds)
    {
        var (start, end, amount) = milliseconds < 60
            ? (Color.FromRgb(0x2C, 0x83, 0x48), Color.FromRgb(0x60, 0x92, 0x35), milliseconds / 60)
            : milliseconds < 300
                ? (Color.FromRgb(0xA5, 0x8B, 0x1C), Color.FromRgb(0xBE, 0x78, 0x1C), (milliseconds - 60) / 240)
                : (Color.FromRgb(0xBE, 0x43, 0x35), Color.FromRgb(0x86, 0x25, 0x28), Math.Min((milliseconds - 300) / 700, 1));
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        var brush = new SolidColorBrush(Color.FromRgb(Mix(start.R, end.R), Mix(start.G, end.G), Mix(start.B, end.B)));
        brush.Freeze();
        return brush;
    }
    public void SetState(bool exists, bool running, bool enabled)
    {
        ActionHint = running ? (enabled ? "暂停此应用直连" : "开启此应用直连") : !exists ? "替换失效入口" : "启动应用";
        PropertyChanged?.Invoke(this, new(nameof(ActionHint)));
        Status = !exists ? "入口失效 · 拖入替换" : running ? (enabled ? "直连已启用 · 等待连接观测" : "跟随系统") : "点击图标启动";
        StatusBrush = (Brush)Application.Current.FindResource(!exists ? "StatusWarning" : running && enabled ? "StatusSuccess" : "Muted");
        PropertyChanged?.Invoke(this, new(nameof(Status)));
        PropertyChanged?.Invoke(this, new(nameof(StatusBrush)));
    }
}

internal static class ApplicationIcon
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon; public int IconIndex; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[]? small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    public static ImageSource? Read(RegisteredApplication entry)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrWhiteSpace(entry.IconPath))
            {
                var large = new IntPtr[1];
                ExtractIconEx(Environment.ExpandEnvironmentVariables(entry.IconPath), entry.IconIndex, large, null, 1);
                handle = large[0];
            }
            if (handle == IntPtr.Zero)
            {
                var info = new ShellFileInfo();
                SHGetFileInfo(entry.Path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100);
                handle = info.Icon;
            }
            if (handle == IntPtr.Zero) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(48, 48));
            image.Freeze();
            return image;
        }
        catch { return null; }
        finally { if (handle != IntPtr.Zero) DestroyIcon(handle); }
    }
}
