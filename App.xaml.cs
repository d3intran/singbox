using System.Windows;
using Wpf.Ui.Appearance;

namespace SingBoxTrayApp;

public partial class App : System.Windows.Application
{
    private static System.Threading.Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool createdNew;
        _mutex = new System.Threading.Mutex(true, "SingBoxTrayApp-Unique-Mutex-Name", out createdNew);

        if (!createdNew)
        {
            System.Windows.MessageBox.Show("Sing-Box 已经在后台运行中！", "提示", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            System.Environment.Exit(0);
            return;
        }

        System.Environment.CurrentDirectory = System.AppDomain.CurrentDomain.BaseDirectory;
        
        this.DispatcherUnhandledException += (s, ev) => 
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.Environment.CurrentDirectory, "crash.log"), ev.Exception.ToString());
            System.Windows.MessageBox.Show(ev.Exception.Message, "Fatal Error");
            System.Environment.Exit(1);
        };

        try
        {
            ApplicationThemeManager.ApplySystemTheme();
        }
        catch (System.Exception ex)
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.Environment.CurrentDirectory, "theme_crash.log"), ex.ToString());
        }

        base.OnStartup(e);
    }
}
