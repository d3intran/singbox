using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace SingBoxTrayApp.Views;

public partial class HomePage : Page
{
    private DispatcherTimer? _statsTimer;

    public HomePage()
    {
        InitializeComponent();
        
        Loaded += (s, e) => {
            // Bind core state notifications
            SingBoxService.Instance.StatusChanged += UpdateStatus;
            
            // Stats refresh timer
            _statsTimer = new DispatcherTimer();
            _statsTimer.Interval = TimeSpan.FromSeconds(1.5);
            _statsTimer.Tick += (sender, args) => UpdateStats();
            _statsTimer.Start();

            UpdateStatus();
            UpdateStats();
            RefreshNodes();
        };

        Unloaded += (s, e) => {
            SingBoxService.Instance.StatusChanged -= UpdateStatus;
            _statsTimer?.Stop();
        };
    }

    private void UpdateStatus()
    {
        bool isRunning = SingBoxService.Instance.IsRunning;
        
        // Sync the Apple Switch ToggleButton state
        if (BtnAppleToggle.IsChecked != isRunning)
        {
            BtnAppleToggle.IsChecked = isRunning;
        }

        if (isRunning)
        {
            // Connected state
            TxtStatusTitle.Text = "已连接";
            TxtStatusTitle.Foreground = ProxyNodeViewModel.GreenForeground; // reuse cached brush
        }
        else
        {
            // Disconnected state
            TxtStatusTitle.Text = "未连接";
            TxtStatusTitle.Foreground = System.Windows.Application.Current.Resources["TextFillColorPrimaryBrush"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black;
        }

        TxtActiveNode.Text = SingBoxService.Instance.ActiveNodeName;
        RefreshNodes();
    }

    private void UpdateStats()
    {
        // 1. Core RAM Footprint
        double mem = SingBoxService.Instance.MemoryUsageMB;
        TxtMemory.Text = $"{mem:F1} MB";

        // 2. Latency of current node
        var activeNodeName = SingBoxService.Instance.ActiveNodeName;
        var activeNode = SingBoxService.Instance.ProxyNodes.Find(n => n.Name == activeNodeName);
        if (activeNode != null && activeNode.Delay > 0)
        {
            TxtLatency.Text = $"{activeNode.Delay} ms";
        }
        else if (SingBoxService.Instance.IsRunning)
        {
            TxtLatency.Text = "已就绪";
        }
        else
        {
            TxtLatency.Text = "-- ms";
        }

        // 3. Loaded nodes count
        TxtNodeCount.Text = $"{SingBoxService.Instance.ProxyNodes.Count} 个";
    }

    private async void BtnAppleToggle_Click(object sender, RoutedEventArgs e)
    {
        BtnAppleToggle.IsEnabled = false;
        if (SingBoxService.Instance.IsRunning)
        {
            SingBoxService.Instance.Stop();
        }
        else
        {
            await SingBoxService.Instance.StartAsync();
        }
        BtnAppleToggle.IsEnabled = true;
        UpdateStatus();
        UpdateStats();
    }

    // Track previous state to avoid unnecessary full rebuilds
    private int _lastNodeCount = -1;
    private string _lastActiveNode = "";
    private string _lastFilter = "";

    private void RefreshNodes()
    {
        var activeNodeName = SingBoxService.Instance.ActiveNodeName;
        var filter = TxtSearch.Text.Trim();
        var nodes = SingBoxService.Instance.ProxyNodes;

        // Skip rebuild if nothing changed (same nodes, same active, same filter)
        if (nodes.Count == _lastNodeCount && activeNodeName == _lastActiveNode && filter == _lastFilter
            && NodesList.ItemsSource != null)
        {
            return;
        }

        _lastNodeCount = nodes.Count;
        _lastActiveNode = activeNodeName;
        _lastFilter = filter;

        var viewModels = new List<ProxyNodeViewModel>(nodes.Count);
        foreach (var node in nodes)
        {
            if (!string.IsNullOrEmpty(filter) && !node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            bool isActive = (node.Name == activeNodeName && SingBoxService.Instance.IsRunning);
            viewModels.Add(new ProxyNodeViewModel(node, isActive));
        }

        NodesList.ItemsSource = viewModels;
    }

    private async void BtnTest_Click(object sender, RoutedEventArgs e)
    {
        BtnTest.IsEnabled = false;
        BtnTest.Content = "测速中...";
        // Force refresh after test since delays change
        _lastNodeCount = -1;
        await SingBoxService.Instance.RunLatencyTestsAsync();
        BtnTest.Content = "一键测速";
        BtnTest.IsEnabled = true;
        RefreshNodes();
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _lastFilter = ""; // invalidate cache to force rebuild
        RefreshNodes();
    }

    private async void NodeCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Border border && border.DataContext is ProxyNodeViewModel vm)
        {
            _lastActiveNode = ""; // invalidate cache
            await SingBoxService.Instance.SwitchNodeAsync(vm.RawNode.Name);
            RefreshNodes();
        }
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer scv)
        {
            scv.ScrollToVerticalOffset(scv.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }
}

/// <summary>
/// Premium ViewModel with static frozen Brush instances to eliminate per-access GC allocations.
/// All Brush objects are created once, frozen (made immutable), and reused across all instances.
/// </summary>
public class ProxyNodeViewModel
{
    // ── Static Frozen Brush Cache ──────────────────────────────────────
    // Frozen brushes are thread-safe and avoid repeated allocations.
    // Each brush is created once at class load and shared by ALL ViewModel instances.

    // Pill backgrounds
    private static readonly SolidColorBrush s_pillBgDefault = Freeze(new SolidColorBrush(Color.FromArgb(20, 120, 120, 125)));
    private static readonly SolidColorBrush s_pillBgGreen   = Freeze(new SolidColorBrush(Color.FromArgb(25, 48, 209, 88)));
    private static readonly SolidColorBrush s_pillBgAmber   = Freeze(new SolidColorBrush(Color.FromArgb(25, 255, 185, 0)));
    private static readonly SolidColorBrush s_pillBgRed     = Freeze(new SolidColorBrush(Color.FromArgb(25, 255, 59, 48)));

    // Pill foregrounds
    private static readonly SolidColorBrush s_pillFgDefault = Freeze(new SolidColorBrush(Color.FromRgb(140, 140, 145)));
    private static readonly SolidColorBrush s_pillFgGreen   = Freeze(new SolidColorBrush(Color.FromRgb(48, 209, 88)));
    private static readonly SolidColorBrush s_pillFgAmber   = Freeze(new SolidColorBrush(Color.FromRgb(255, 185, 0)));
    private static readonly SolidColorBrush s_pillFgRed     = Freeze(new SolidColorBrush(Color.FromRgb(255, 59, 48)));

    // Status title color (reused by HomePage)
    public static readonly SolidColorBrush GreenForeground = Freeze(new SolidColorBrush(Color.FromRgb(48, 209, 88)));

    private static SolidColorBrush Freeze(SolidColorBrush brush) { brush.Freeze(); return brush; }

    // ── Instance Members ───────────────────────────────────────────────
    private readonly ProxyNode _node;
    private readonly bool _isActive;

    public ProxyNodeViewModel(ProxyNode node, bool isActive)
    {
        _node = node;
        _isActive = isActive;
    }

    public string Name => _node.Name;
    public int Delay => _node.Delay;

    public string DelayText
    {
        get
        {
            if (Delay == 0 || Delay == -1) return "未测试";
            if (Delay == -2) return "超时";
            return $"{Delay} ms";
        }
    }

    public Brush PillBackground
    {
        get
        {
            if (Delay <= 0) return s_pillBgDefault;
            if (Delay < 300) return s_pillBgGreen;
            if (Delay < 800) return s_pillBgAmber;
            return s_pillBgRed;   // ≥800ms = red timeout territory
        }
    }

    public Brush PillForeground
    {
        get
        {
            if (Delay <= 0) return s_pillFgDefault;
            if (Delay < 300) return s_pillFgGreen;
            if (Delay < 800) return s_pillFgAmber;
            return s_pillFgRed;
        }
    }

    public Brush CardBorderBrush
    {
        get
        {
            if (_isActive) 
                return System.Windows.Application.Current.Resources["SystemAccentColorBrush"] as Brush ?? Brushes.DodgerBlue;
            
            return System.Windows.Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush ?? Brushes.Gray;
        }
    }

    public Thickness CardBorderThickness => _isActive ? new Thickness(1.5) : new Thickness(1);

    public double ShadowOpacity => _isActive ? 0.15 : 0.02;

    public Visibility ActiveIconVisibility => _isActive ? Visibility.Visible : Visibility.Collapsed;

    public ProxyNode RawNode => _node;
}
