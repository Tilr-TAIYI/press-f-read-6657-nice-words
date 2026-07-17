using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sb6657Cs2Assistant;

/// <summary>只负责观察 CS2 进程和前台状态；不向游戏注入键盘输入。</summary>
public sealed class Cs2RuntimeService
{
    public bool IsCs2Running => Process.GetProcessesByName("cs2").Length > 0;

    public bool IsCs2Foreground
    {
        get
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return false;
            GetWindowThreadProcessId(window, out var processId);
            try
            {
                using var process = Process.GetProcessById((int)processId);
                return process.ProcessName.Equals("cs2", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
