using System;
using System.Windows;
using System.Windows.Controls;

namespace SingBoxTrayApp.Views;

public partial class SettingsPage : Page
{
    // Maximum character count allowed in the log TextBox.
    // Prevents unbounded memory growth when the user stays on this page for a long time.
    private const int MaxLogTextLength = 50_000;
    private const int TrimToLength     = 30_000;

    private System.Windows.Threading.DispatcherTimer? _uiTimer;

    public SettingsPage()
    {
        InitializeComponent();

        _uiTimer = new System.Windows.Threading.DispatcherTimer();
        _uiTimer.Interval = TimeSpan.FromSeconds(10);
        _uiTimer.Tick += (s, e) => UpdateSyncTimeDisplay();
        
        Loaded += (s, e) => {
            // Load buffered logs so logs persist when switching tabs!
            TxtLog.Clear();
            lock (SingBoxService.Instance.LogBuffer)
            {
                foreach (var line in SingBoxService.Instance.LogBuffer)
                {
                    TxtLog.AppendText(line + "\n");
                }
            }
            TxtLog.ScrollToEnd();
            
            SingBoxService.Instance.LogReceived += AppendLog;

            UpdateSyncTimeDisplay();
            _uiTimer.Start();
        };
        
        Unloaded += (s, e) => {
            SingBoxService.Instance.LogReceived -= AppendLog;
            _uiTimer.Stop();
        };
    }

    private void UpdateSyncTimeDisplay()
    {
        TxtLastSyncTime.Text = GetRelativeTimeString(SingBoxService.Instance.LastSyncTime);
    }

    private string GetRelativeTimeString(DateTime? lastSyncTime)
    {
        if (lastSyncTime == null) return "从未同步";
        
        var diff = DateTime.Now - lastSyncTime.Value;
        if (diff.TotalSeconds < 0) return "刚刚";
        
        if (diff.TotalMinutes < 1)
        {
            return "刚刚";
        }
        else if (diff.TotalHours < 1)
        {
            int minutes = (int)Math.Floor(diff.TotalMinutes);
            return $"{minutes}分钟前";
        }
        else if (diff.TotalDays < 1)
        {
            int hours = (int)Math.Floor(diff.TotalHours);
            return $"{hours}小时前";
        }
        else
        {
            int days = (int)Math.Floor(diff.TotalDays);
            return $"{days}天前";
        }
    }

    private void AppendLog(string msg)
    {
        TxtLog.AppendText(msg + "\n");

        // Cap TextBox text length to prevent unbounded growth.
        // When the limit is exceeded, trim the oldest portion.
        if (TxtLog.Text.Length > MaxLogTextLength)
        {
            var text = TxtLog.Text;
            // Find a newline boundary near the trim point to avoid cutting a line in half
            int trimStart = text.Length - TrimToLength;
            int newlinePos = text.IndexOf('\n', trimStart);
            if (newlinePos >= 0 && newlinePos < text.Length - 1)
                trimStart = newlinePos + 1;

            TxtLog.Text = text.Substring(trimStart);
        }

        TxtLog.ScrollToEnd();
    }

    private async void BtnPull_Click(object sender, RoutedEventArgs e)
    {
        BtnPull.IsEnabled = false;
        BtnPull.Content = "正在拉取...";
        await SingBoxService.Instance.UpdateConfigAsync();
        UpdateSyncTimeDisplay();
        BtnPull.Content = "同步云端配置";
        BtnPull.IsEnabled = true;
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(TxtLog.Text))
            {
                System.Windows.Clipboard.SetText(TxtLog.Text);
                System.Windows.MessageBox.Show("日志已成功复制到剪贴板！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"复制失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        TxtLog.Clear();
        lock (SingBoxService.Instance.LogBuffer)
        {
            SingBoxService.Instance.LogBuffer.Clear();
        }
    }
}
