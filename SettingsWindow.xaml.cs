using System.Windows;
using System.Windows.Input;

namespace Tumbler;

/// <summary>Настройки применяются сразу, по мере изменения; кнопки «ОК» нет.</summary>
public partial class SettingsWindow : Window
{
    readonly Settings _settings;
    readonly bool _canChangeSystem;
    readonly Func<string, bool> _registerHotkey;
    readonly Action _changed;
    readonly Action<Window>? _uninstall;

    /// <param name="canChangeSystem">Есть ли права на создание задачи автозапуска.</param>
    /// <param name="registerHotkey">Пробует занять сочетание; false — оно уже занято другой программой.</param>
    /// <param name="changed">Вызывается после каждого изменения настроек.</param>
    /// <param name="uninstall">Удаление установленных компонентов; null — кнопка скрыта.</param>
    internal SettingsWindow(Settings settings, bool canChangeSystem, Func<string, bool> registerHotkey, Action changed, Action<Window>? uninstall)
    {
        InitializeComponent();
        _settings = settings;
        _canChangeSystem = canChangeSystem;
        _registerHotkey = registerHotkey;
        _changed = changed;
        _uninstall = uninstall;

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        AboutText.Text = $"Тумблер {version?.ToString(3)}. Внутри — ProxiFyre {Setup.ProxiFyreVersion} (лицензия AGPL-3.0, исходный код: github.com/wiresock/proxifyre) " +
                         $"и драйвер Windows Packet Filter {Setup.DriverVersion} (NT KERNEL, бесплатен для личного некоммерческого использования).";
        UninstallButton.Visibility = uninstall is null ? Visibility.Collapsed : Visibility.Visible;

        BlockRadio.IsChecked = !settings.FallbackDirect;
        DirectRadio.IsChecked = settings.FallbackDirect;
        CloseToTrayBox.IsChecked = settings.CloseToTray;
        AutostartBox.IsChecked = Autostart.IsEnabled();
        AutostartBox.IsEnabled = canChangeSystem;
        HotkeyBox.Text = settings.Hotkey.Length == 0 ? "отключена" : settings.Hotkey;

        SourceInitialized += (_, _) => Chrome.UseDarkTitleBar(this);
    }

    void Fallback_Click(object sender, RoutedEventArgs e)
    {
        _settings.FallbackDirect = DirectRadio.IsChecked == true;
        _changed();

        // Возврат в VPN делает сам «Тумблер», поэтому без автозапуска после перезагрузки он не сработает.
        if (_settings.FallbackDirect && AutostartBox.IsChecked != true)
            ShowAutostartNote("Советую включить автозапуск: после перезагрузки вернуть приложения в VPN сможет только запущенный «Тумблер».");
    }

    void CloseToTray_Click(object sender, RoutedEventArgs e)
    {
        _settings.CloseToTray = CloseToTrayBox.IsChecked == true;
        _changed();
    }

    void Autostart_Click(object sender, RoutedEventArgs e)
    {
        bool wanted = AutostartBox.IsChecked == true;
        if (Autostart.Set(wanted))
        {
            AutostartNote.Visibility = Visibility.Collapsed;
            return;
        }
        AutostartBox.IsChecked = !wanted;
        ShowAutostartNote("Не получилось изменить задачу в планировщике Windows.");
    }

    void ShowAutostartNote(string text)
    {
        AutostartNote.Text = text;
        AutostartNote.Visibility = Visibility.Visible;
    }

    void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.Tab or Key.Escape) return;

        var modifiers = Keyboard.Modifiers;
        if (modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            HotkeyNote.Text = "Нужно сочетание с Ctrl или Alt, например Ctrl+Alt+P.";
            return;
        }

        SetHotkey(Hotkey.Format(modifiers, key));
    }

    void HotkeyOff_Click(object sender, RoutedEventArgs e) => SetHotkey("");

    void SetHotkey(string combo)
    {
        if (combo.Length > 0 && !_registerHotkey(combo))
        {
            HotkeyNote.Text = $"Сочетание {combo} уже занято другой программой — выбери другое.";
            _registerHotkey(_settings.Hotkey);
            return;
        }
        if (combo.Length == 0) _registerHotkey("");

        _settings.Hotkey = combo;
        HotkeyBox.Text = combo.Length == 0 ? "отключена" : combo;
        HotkeyNote.Text = "Ставит всё на паузу и снимает с неё из любой программы.";
        _changed();
    }

    void Uninstall_Click(object sender, RoutedEventArgs e) => _uninstall?.Invoke(this);

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
