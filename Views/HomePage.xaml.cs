using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using Wpf.Ui.Controls;

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
            TxtStatusTitle.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 209, 88)); // Apple iOS Green
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

    private void RefreshNodes()
    {
        var activeNodeName = SingBoxService.Instance.ActiveNodeName;
        var filter = TxtSearch.Text.Trim();

        var viewModels = new List<ProxyNodeViewModel>();
        foreach (var node in SingBoxService.Instance.ProxyNodes)
        {
            if (!string.IsNullOrEmpty(filter) && !node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            bool isActive = (node.Name == activeNodeName && SingBoxService.Instance.IsRunning);
            viewModels.Add(new ProxyNodeViewModel(node, isActive));
        }

        NodesList.ItemsSource = null;
        NodesList.ItemsSource = viewModels;
    }

    private async void BtnTest_Click(object sender, RoutedEventArgs e)
    {
        BtnTest.IsEnabled = false;
        BtnTest.Content = "测速中...";
        await SingBoxService.Instance.RunLatencyTestsAsync();
        BtnTest.Content = "一键测速";
        BtnTest.IsEnabled = true;
        RefreshNodes();
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshNodes();
    }

    private async void NodeCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Border border && border.DataContext is ProxyNodeViewModel vm)
        {
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
/// Premium ViewModel with Apple-Green and Amber-Yellow latency thresholds
/// </summary>
public class ProxyNodeViewModel
{
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

    public System.Windows.Media.Brush PillBackground
    {
        get
        {
            if (Delay <= 0) return new SolidColorBrush(System.Windows.Media.Color.FromArgb(20, 120, 120, 125));
            if (Delay < 300) return new SolidColorBrush(System.Windows.Media.Color.FromArgb(25, 48, 209, 88)); // Apple iOS Green (<300ms)
            return new SolidColorBrush(System.Windows.Media.Color.FromArgb(25, 255, 185, 0));  // iOS Amber/Yellow (>=300ms)
        }
    }

    public System.Windows.Media.Brush PillForeground
    {
        get
        {
            if (Delay <= 0) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(140, 140, 145));
            if (Delay < 300) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 209, 88)); // Apple iOS Green (<300ms)
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 185, 0));  // iOS Amber/Yellow (>=300ms)
        }
    }

    public System.Windows.Media.Brush CardBorderBrush
    {
        get
        {
            if (_isActive) 
                return System.Windows.Application.Current.Resources["SystemAccentColorBrush"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.DodgerBlue;
            
            return System.Windows.Application.Current.Resources["CardStrokeColorDefaultBrush"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
        }
    }

    public Thickness CardBorderThickness => _isActive ? new Thickness(1.5) : new Thickness(1);

    public double ShadowOpacity => _isActive ? 0.15 : 0.02;

    public Visibility ActiveIconVisibility => _isActive ? Visibility.Visible : Visibility.Collapsed;

    public ProxyNode RawNode => _node;
}
