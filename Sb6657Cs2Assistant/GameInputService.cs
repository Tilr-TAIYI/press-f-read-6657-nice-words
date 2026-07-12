using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Sb6657Cs2Assistant;

public sealed class GameInputService
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkReturn = 0x0D;

    public bool IsCs2Running => Process.GetProcessesByName("cs2").Length > 0;

    public bool IsCs2Foreground
    {
        get
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out var pid);
            try { return Process.GetProcessById((int)pid).ProcessName.Equals("cs2", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }

    public bool TryFocusCs2()
    {
        var process = Process.GetProcessesByName("cs2")
            .FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero);
        if (process is null) return false;
        ShowWindow(process.MainWindowHandle, 9);
        if (!SetForegroundWindow(process.MainWindowHandle)) return false;
        return true;
    }

    public async Task<bool> SendChatAsync(string message, string chatKey, CancellationToken token)
    {
        if (!IsCs2Foreground) return false;

        string? previousText = null;
        var hadText = false;
        try
        {
            if (Clipboard.ContainsText())
            {
                previousText = Clipboard.GetText();
                hadText = true;
            }
            Clipboard.SetText(message);

            var chatVk = ParseChatKey(chatKey);
            Press(chatVk);
            await Task.Delay(180, token);
            if (!IsCs2Foreground) return false;

            Key(VkControl, false);
            Press((ushort)Keys.V);
            Key(VkControl, true);
            await Task.Delay(140, token);
            if (!IsCs2Foreground) return false;

            Press(VkReturn);
            return true;
        }
        finally
        {
            try
            {
                if (hadText && previousText is not null) Clipboard.SetText(previousText);
            }
            catch { }
        }
    }

    public async Task<bool> TriggerBoundKeyAsync(string key, CancellationToken token)
    {
        if (!IsCs2Foreground) return false;
        var virtualKey = ParseChatKey(key);
        Key(virtualKey, false);
        await Task.Delay(60, token);
        Key(virtualKey, true);
        await Task.Delay(100, token);
        return IsCs2Foreground;
    }

    private static ushort ParseChatKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (ushort)Keys.Y;
        var text = value.Trim();
        if (Enum.TryParse<Keys>(text, true, out var key) && key != Keys.None) return (ushort)key;
        var mapped = VkKeyScan(text[0]);
        if (mapped == -1) throw new ArgumentException($"无法识别聊天键：{value}");
        return (ushort)(mapped & 0xFF);
    }

    private static void Press(ushort key)
    {
        Key(key, false);
        Key(key, true);
    }

    private static void Key(ushort key, bool up)
    {
        var input = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? KeyUp : 0 }
            }
        };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
        {
            // Some desktop security policies reject SendInput even at matching integrity.
            // keybd_event remains useful as a compatibility fallback for local game input.
            keybd_event((byte)key, 0, up ? KeyUp : 0, UIntPtr.Zero);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public InputUnion Data;
    }

    // Native INPUT uses the size of its largest union member (MOUSEINPUT): 32 bytes on x64.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScan(char character);
    [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
