using System.Windows;
using Wpf.Ui.Controls;

namespace SingBoxTrayApp;

public partial class MainWindow : FluentWindow
{
    private System.Windows.Forms.NotifyIcon _notifyIcon;

    public MainWindow()
    {
        // Register dynamic blue-tinted theme keys before XAML begins parsing
        ApplySubtleBlueTheme();

        InitializeComponent();

        // Listen for theme changes dynamically to update colors in real-time
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += (theme, accent) =>
        {
            ApplySubtleBlueTheme();
        };

        // Position window in the bottom-right corner of the screen (above the taskbar)
        var desktopWorkingArea = SystemParameters.WorkArea;
        double width = this.Width > 0 ? this.Width : 430;
        double height = this.Height > 0 ? this.Height : 680;
        
        // Use an elegant 60px right margin and 40px bottom margin to float comfortably 
        // and ensure the entire window is 100% visible on all DPI/screen scaling setups.
        this.Left = desktopWorkingArea.Right - width - 60;
        this.Top = desktopWorkingArea.Bottom - height - 40;

        // Ensure perfect DPI-aware window placement after window handles are fully initialized
        SourceInitialized += (s, e) =>
        {
            var workingArea = SystemParameters.WorkArea;
            double w = this.ActualWidth > 0 ? this.ActualWidth : (this.Width > 0 ? this.Width : 430);
            double h = this.ActualHeight > 0 ? this.ActualHeight : (this.Height > 0 ? this.Height : 680);
            this.Left = workingArea.Right - w - 60;
            this.Top = workingArea.Bottom - h - 40;
        };

        // Setup tray icon from embedded resource (no file needed in output directory)
        _notifyIcon = new System.Windows.Forms.NotifyIcon();
        var iconStream = System.Windows.Application.GetResourceStream(new System.Uri("pack://application:,,,/icon.ico"));
        if (iconStream?.Stream != null)
        {
            using var rawIcon = new System.Drawing.Icon(iconStream.Stream);
            _notifyIcon.Icon = (System.Drawing.Icon)rawIcon.Clone();
        }
        else
        {
            _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
        }
        _notifyIcon.Text = "sinbox";
        _notifyIcon.Visible = true;
        _notifyIcon.DoubleClick += (s, e) => ShowWindow();

        var contextMenu = new System.Windows.Forms.ContextMenuStrip();
        contextMenu.Items.Add("显示主窗口", null, (s, e) => ShowWindow());
        contextMenu.Items.Add("退出", null, (s, e) => {
            SingBoxService.Instance.Stop();
            _notifyIcon.Dispose();
            System.Windows.Application.Current.Shutdown();
        });
        _notifyIcon.ContextMenuStrip = contextMenu;

        Closing += (s, e) => {
            e.Cancel = true;
            this.Hide();
        };

        // CRITICAL: Navigate MUST happen in Loaded, NOT in constructor.
        // The NavigationView's internal Frame/ContentPresenter is null until
        // the visual tree is fully constructed (after Loaded fires).
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            RootNavigation.Navigate(typeof(Views.HomePage));
        }
        catch (System.Exception ex)
        {
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nav_crash.log"),
                ex.ToString());

            // Extract the most specific inner exception details to help users debug
            var innerEx = ex;
            while (innerEx.InnerException != null)
            {
                innerEx = innerEx.InnerException;
            }

            System.Windows.MessageBox.Show(
                $"主页加载失败: {innerEx.Message}\n\n详细异常已写入 nav_crash.log，请联系管理员处理。",
                "Fatal Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void ShowWindow()
    {
        this.Show();
        if (this.WindowState == WindowState.Minimized) this.WindowState = WindowState.Normal;
        this.Activate();
    }

    private void ApplySubtleBlueTheme()
    {
        var currentTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme();
        
        // System-wide Accent Color: Modern Fluent Blue (#0078D4) matching Microsoft PC Manager
        try
        {
            Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(System.Windows.Media.Color.FromRgb(0, 120, 212));
        }
        catch { }

        if (currentTheme == Wpf.Ui.Appearance.ApplicationTheme.Light)
        {
            // Ice Sky-Blue main background (Solid, beautiful sky-blue)
            var lightBgBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 220, 248));
            this.Background = lightBgBrush;
            
            System.Windows.Application.Current.Resources["WindowSubtleBlueBg"] = lightBgBrush;
            System.Windows.Application.Current.Resources["CardSubtleBg"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255));
            System.Windows.Application.Current.Resources["CardStrokeColorDefaultBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(172, 198, 236));
        }
        else
        {
            // Sleek deep starry-night dark blue background (Solid deep navy)
            var darkBgBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(10, 15, 26));
            this.Background = darkBgBrush;
            
            System.Windows.Application.Current.Resources["WindowSubtleBlueBg"] = darkBgBrush;
            System.Windows.Application.Current.Resources["CardSubtleBg"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 24, 38));
            System.Windows.Application.Current.Resources["CardStrokeColorDefaultBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 38, 59));
        }
    }
}
