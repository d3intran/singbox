using System;
using System.Windows;
using System.Windows.Controls;

namespace SingBoxTrayApp.Views;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        
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
        };
        
        Unloaded += (s, e) => SingBoxService.Instance.LogReceived -= AppendLog;
    }

    private void AppendLog(string msg)
    {
        TxtLog.AppendText(msg + "\n");
        TxtLog.ScrollToEnd();
    }

    private async void BtnPull_Click(object sender, RoutedEventArgs e)
    {
        BtnPull.IsEnabled = false;
        BtnPull.Content = "正在拉取...";
        await SingBoxService.Instance.UpdateConfigAsync();
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
