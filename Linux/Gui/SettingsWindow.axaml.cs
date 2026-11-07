using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Tumbler.Daemon;

namespace Tumbler.Gui;

/// <summary>Настройки применяются сразу, по мере изменения; кнопки «ОК» нет.</summary>
public partial class SettingsWindow : Window
{
    readonly Settings _settings;
    readonly Action<bool> _fallbackChanged;
    readonly Action<Window>? _uninstall;
    readonly bool _ready;

    public SettingsWindow() : this(new Settings(), false, _ => { }, null)
    {
    }

    /// <param name="fallbackDirect">Текущее поведение службы при недоступном VPN.</param>
    /// <param name="fallbackChanged">Отправляет новое поведение службе.</param>
    /// <param name="uninstall">Удаление службы; null — кнопка скрыта.</param>
    internal SettingsWindow(Settings settings, bool fallbackDirect, Action<bool> fallbackChanged, Action<Window>? uninstall)
    {
        InitializeComponent();
        _settings = settings;
        _fallbackChanged = fallbackChanged;
        _uninstall = uninstall;
        Icon = App.LoadIcon();

        BlockRadio.IsChecked = !fallbackDirect;
        DirectRadio.IsChecked = fallbackDirect;
        CloseToTrayBox.IsChecked = settings.CloseToTray;
        ToggleCommand.Text = $"{Installer.ExePath} toggle";
        UninstallButton.IsVisible = uninstall is not null;

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        AboutText.Text = $"Тумблер для Linux {version}. Внутри — hev-socks5-tunnel {Installer.HevVersion} (лицензия MIT, github.com/heiher/hev-socks5-tunnel). " +
                         "Журнал службы: journalctl -u tumbler. Состояние из терминала: tumbler status.";
        _ready = true;
    }

    void Fallback_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { IsChecked: true }) return;
        _fallbackChanged(DirectRadio.IsChecked == true);
    }

    void CloseToTray_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.CloseToTray = CloseToTrayBox.IsChecked == true;
        _settings.Save();
    }

    async void CopyCommand_Click(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(ToggleCommand.Text);
    }

    void Uninstall_Click(object? sender, RoutedEventArgs e) => _uninstall?.Invoke(this);

    void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
