using System.Threading;
using System.Windows;
using System.IO;

namespace Sb6657Cs2Assistant;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            System.Windows.MessageBox.Show($"程序发生错误：{args.Exception.Message}\n\n详细信息已写入 LocalAppData 日志，程序将继续运行。", "CS2 烂梗助手", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        _singleInstance = new Mutex(true, "Sb6657Cs2Assistant.SingleInstance", out var created);
        if (!created)
        {
            System.Windows.MessageBox.Show("CS2 烂梗助手已经在运行。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        try
        {
            _window = new MainWindow();
            _window.Show();
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            System.Windows.MessageBox.Show($"控制面板启动失败：{ex.Message}\n\n详细信息已写入 LocalAppData 日志。", "CS2 烂梗助手", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sb6657Cs2Assistant");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "crash.log"), $"[{DateTime.Now:O}]\n{exception}\n\n");
        }
        catch { }
    }
}
