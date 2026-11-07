using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Tumbler;

/// <summary>Глобальная горячая клавиша: работает, даже когда окно «Тумблера» скрыто в трее.</summary>
internal sealed class Hotkey : IDisposable
{
    const int WM_HOTKEY = 0x0312, Id = 0x7B01;
    const uint MOD_NOREPEAT = 0x4000;

    readonly HwndSource _source;
    bool _registered;

    public Hotkey(IntPtr windowHandle)
    {
        _source = HwndSource.FromHwnd(windowHandle)!;
        _source.AddHook(Hook);
    }

    public event Action? Pressed;

    /// <summary>Регистрирует сочетание вида «Ctrl+Alt+P». false — оно занято другой программой или записано неверно.</summary>
    public bool Register(string combo)
    {
        Unregister();
        if (!TryParse(combo, out var modifiers, out var key)) return false;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        _registered = RegisterHotKey(_source.Handle, Id, (uint)modifiers | MOD_NOREPEAT, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered) UnregisterHotKey(_source.Handle, Id);
        _registered = false;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(Hook);
    }

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    static bool TryParse(string combo, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        foreach (var part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part)
            {
                case "Ctrl": modifiers |= ModifierKeys.Control; break;
                case "Alt": modifiers |= ModifierKeys.Alt; break;
                case "Shift": modifiers |= ModifierKeys.Shift; break;
                case "Win": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (!Enum.TryParse(part, out key)) return false;
                    break;
            }
        }
        return key != Key.None && modifiers != ModifierKeys.None;
    }

    IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_HOTKEY && wParam.ToInt32() == Id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
