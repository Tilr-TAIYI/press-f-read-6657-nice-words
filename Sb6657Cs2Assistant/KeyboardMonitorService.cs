using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Sb6657Cs2Assistant;

public sealed class KeyboardMonitorService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;
    private const uint LlkhfInjected = 0x10;
    private readonly HookProc _callback;
    private IntPtr _hook;
    private uint _watchedKey = (uint)Keys.F8;

    public event Action? WatchedKeyReleased;

    public KeyboardMonitorService() => _callback = HookCallback;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new InvalidOperationException("无法启动发送键监听");
    }

    public void SetWatchedKey(string key)
    {
        if (Enum.TryParse<Keys>(key, true, out var parsed) && parsed != Keys.None)
            _watchedKey = (uint)parsed;
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam.ToInt32() == WmKeyUp || wParam.ToInt32() == WmSysKeyUp))
        {
            var data = Marshal.PtrToStructure<KeyboardHookData>(lParam);
            if (data.VirtualKey == _watchedKey && (data.Flags & LlkhfInjected) == 0)
                WatchedKeyReleased?.Invoke();
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
}
