using System.ComponentModel;
using System.Net.Http;
using System.Windows;

namespace Tumbler;

/// <summary>
/// Мастер первого запуска: показывает, что будет установлено, и ставит это по кнопке.
/// Ничего не делает молча — пользователь видит список и сам нажимает «Установить».
/// </summary>
public partial class SetupWindow : Window
{
    bool _busy, _done;

    /// <param name="items">Готовый список для отладочного снимка; обычно берётся из <see cref="Setup.Check"/>.</param>
    internal SetupWindow(List<SetupItem>? items = null)
    {
        InitializeComponent();
        Icon = Chrome.DrawWindowIcon();
        Items.ItemsSource = items ?? Setup.Check();
        if (!Elevation.IsAdmin) InstallButton.Content = "Запустить с правами администратора";
        SourceInitialized += (_, _) => Chrome.UseDarkTitleBar(this);
    }

    /// <summary>Показывает мастер и возвращает true, если после него всё установлено.</summary>
    internal static bool Run(Window? owner)
    {
        var wizard = new SetupWindow();
        if (owner is not null)
        {
            wizard.Owner = owner;
            wizard.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        return wizard.ShowDialog() == true;
    }

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_done)
        {
            DialogResult = true;
            return;
        }
        if (!Elevation.IsAdmin)
        {
            if (Elevation.RelaunchElevated([])) Application.Current.Shutdown();
            return;
        }

        _busy = true;
        InstallButton.IsEnabled = ExitButton.IsEnabled = false;
        ShowStatus("Начинаю установку…", "Muted");
        try
        {
            var progress = new Progress<string>(text => ShowStatus(text, "Muted"));
            bool reboot = await Task.Run(() => Setup.Install(((IProgress<string>)progress).Report));
            Items.ItemsSource = Setup.Check();
            Legal.Visibility = Visibility.Collapsed;

            if (reboot)
            {
                Headline.Text = "Осталось перезагрузиться";
                Intro.Text = "Всё установлено, но драйвер начнёт работать только после перезагрузки компьютера. Перезагрузись и запусти «Тумблер» снова.";
                Status.Visibility = Visibility.Collapsed;
                InstallButton.Visibility = Visibility.Collapsed;
                ExitButton.Content = "Закрыть";
            }
            else
            {
                _done = true;
                Headline.Text = "Готово";
                Intro.Text = "Что дальше:\n" +
                             "1. Запусти свой VPN-клиент в обычном режиме, не TUN — иначе в VPN уйдёт весь трафик.\n" +
                             "2. В «Тумблере» укажи его SOCKS5-порт. Если клиент уже запущен, «Тумблер» предложит порт сам.\n" +
                             "3. Включи тумблеры у нужных приложений.";
                Status.Visibility = Visibility.Collapsed;
                InstallButton.Content = "Открыть Тумблер";
            }
        }
        catch (Exception ex) when (ex is SetupException or Win32Exception or IOException or UnauthorizedAccessException
                                       or HttpRequestException or InvalidDataException or TimeoutException)
        {
            Items.ItemsSource = Setup.Check();
            ShowStatus($"Не получилось: {ex.Message}\nПодробности — в журнале {Setup.LogFile}", "Danger");
            InstallButton.Content = "Попробовать снова";
        }
        finally
        {
            _busy = false;
            InstallButton.IsEnabled = ExitButton.IsEnabled = true;
        }
    }

    void ShowStatus(string text, string brush)
    {
        Status.Text = text;
        Status.Foreground = Chrome.Brush(brush);
        Status.Visibility = Visibility.Visible;
    }

    void Exit_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Установку драйвера на полпути не прерываем.
        if (_busy) e.Cancel = true;
        base.OnClosing(e);
    }
}
