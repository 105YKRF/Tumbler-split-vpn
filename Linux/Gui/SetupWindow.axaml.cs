using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Tumbler.Daemon;

namespace Tumbler.Gui;

/// <summary>
/// Мастер установки службы. Показывает, чего не хватает системе и что будет поставлено, и по кнопке
/// запускает «tumbler install» через pkexec — с запросом пароля администратора.
/// </summary>
public partial class SetupWindow : Window
{
    readonly IBackend _backend;
    readonly TaskCompletionSource<bool> _completion = new();
    bool _busy, _done;

    public SetupWindow() : this(new DemoBackend())
    {
    }

    internal SetupWindow(IBackend backend)
    {
        InitializeComponent();
        _backend = backend;
        Icon = App.LoadIcon();

        var requirements = backend.IsDemo ? DemoRequirements() : Installer.Check();
        Items.ItemsSource = requirements;
        if (requirements.Any(r => !r.Ok))
        {
            InstallButton.IsEnabled = false;
            ShowStatus("Сначала установи недостающее (команды выше), потом открой «Тумблер» снова.", "Warn");
        }
        else if (!backend.IsDemo && !Pkexec.Available)
        {
            InstallButton.IsEnabled = false;
            ShowStatus("В системе нет pkexec, поэтому окно не может запросить пароль. Выполни в терминале:", "Warn");
            ManualCommand.Text = Pkexec.ManualCommand("install");
            ManualCommand.IsVisible = true;
        }
        Closed += (_, _) => _completion.TrySetResult(_done);
    }

    /// <summary>true — служба установлена и отвечает.</summary>
    internal Task<bool> Completion => _completion.Task;

    static List<Requirement> DemoRequirements() =>
    [
        new("64-битная система на Intel или AMD", "Сборка «Тумблера» — для x86_64.", true),
        new("cgroup v2", "По ней «Тумблер» отличает трафик выбранных приложений. Есть во всех дистрибутивах последних лет.", true),
        new("systemd", "Запускает службу «Тумблера» вместе с системой.", true),
        new("nftables", "Помечает пакеты выбранных приложений.", false, "Установи пакет nftables: sudo apt install nftables"),
        new("iproute2", "Отправляет помеченные пакеты в туннель.", true),
    ];

    async void Install_Click(object? sender, RoutedEventArgs e)
    {
        if (_done)
        {
            Close();
            return;
        }

        _busy = true;
        InstallButton.IsEnabled = ExitButton.IsEnabled = false;
        ShowStatus("Жду пароль администратора…", "Muted");
        var (ok, message) = await Pkexec.RunAsync(["install", "--user", Sys.Uid.ToString()], step => ShowStatus(step + "…", "Muted"));
        _busy = false;
        InstallButton.IsEnabled = ExitButton.IsEnabled = true;

        if (ok && await _backend.CallAsync(new Request()) is not null)
        {
            _done = true;
            Headline.Text = "Готово";
            Intro.Text = "Что дальше:\n" +
                         "1. Запусти свой VPN-клиент в обычном режиме, не TUN — иначе в VPN уйдёт весь трафик.\n" +
                         "2. В «Тумблере» укажи его SOCKS5-порт. Если клиент уже запущен, «Тумблер» предложит порт сам.\n" +
                         "3. Включи тумблеры у нужных приложений.\n\n" +
                         "В следующий раз открывай «Тумблер» из меню приложений.";
            Items.IsVisible = false;
            Status.IsVisible = false;
            InstallButton.Content = "Открыть Тумблер";
            return;
        }

        ShowStatus(ok ? "Установка прошла, но служба не отвечает. Посмотри журнал: journalctl -u tumbler -n 50" : $"Не получилось: {message}", "Danger");
        InstallButton.Content = "Попробовать снова";
    }

    void ShowStatus(string text, string brush)
    {
        Status.Text = text;
        Status.Foreground = (IBrush)this.FindResource(brush)!;
        Status.IsVisible = true;
    }

    void Exit_Click(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Установку на полпути не прерываем.
        if (_busy) e.Cancel = true;
        base.OnClosing(e);
    }
}

/// <summary>Запуск «tumbler install/uninstall» от root через pkexec с разбором строк «STEP …» и «ERROR …».</summary>
internal static class Pkexec
{
    public static bool Available => Sys.Find("pkexec") is not null;

    public static string ManualCommand(string action) => $"sudo \"{Environment.ProcessPath}\" {action} --user {Sys.Uid}";

    public static async Task<(bool Ok, string Message)> RunAsync(string[] arguments, Action<string> onStep)
    {
        if (!Available) return (false, $"в системе нет pkexec. Выполни в терминале: {ManualCommand(arguments[0])}");

        var info = new ProcessStartInfo(Sys.Find("pkexec")!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(Environment.ProcessPath!);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info)!;
            var errors = process.StandardError.ReadToEndAsync();
            string? failure = null;
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("STEP ")) Dispatcher.UIThread.Post(() => onStep(line[5..]));
                else if (line.StartsWith("ERROR ")) failure = line[6..];
            }
            await process.WaitForExitAsync();

            if (process.ExitCode == 0) return (true, "");
            // 126 и 127 — pkexec: пароль не введён или доступ не дан.
            if (failure is null && process.ExitCode is 126 or 127) return (false, "пароль администратора не введён — ничего не изменилось");
            var stderr = (await errors).Trim();
            return (false, failure ?? (stderr.Length > 0 ? stderr : $"код возврата {process.ExitCode}"));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
