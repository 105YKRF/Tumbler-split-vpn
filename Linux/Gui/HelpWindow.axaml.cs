using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Tumbler.Gui;

/// <summary>Справка по настройке VPN-клиентов. Окно не модальное: его держат открытым рядом с главным и идут по шагам.</summary>
public partial class HelpWindow : Window
{
    readonly Func<(List<PortPreset> Proxies, int CurrentPort)> _state;
    readonly Action<int> _usePort;

    public HelpWindow() : this(() => (new List<PortPreset>(), 1080), _ => { })
    {
    }

    /// <param name="state">Какие прокси «Тумблер» сейчас видит и какой порт выбран; перечитывается при возврате в окно.</param>
    /// <param name="usePort">Сделать порт портом по умолчанию.</param>
    /// <param name="topic">Номер раздела; null — раздел запущенного клиента, а если такого нет — «С чего начать».</param>
    internal HelpWindow(Func<(List<PortPreset> Proxies, int CurrentPort)> state, Action<int> usePort, int? topic = null)
    {
        InitializeComponent();
        _state = state;
        _usePort = usePort;
        Icon = App.LoadIcon();

        var topics = HelpContent.Topics();
        Topics.ItemsSource = topics;
        Topics.SelectedIndex = topic is { } index && index < topics.Count ? index : Math.Max(0, topics.FindIndex(t => t.Running));

        Opened += (_, _) => ShowProxies();
        Activated += (_, _) => ShowProxies();
    }

    void ShowProxies()
    {
        var (proxies, port) = _state();
        Found.ItemsSource = proxies;
        FoundText.Text = proxies.Count == 0
            ? $"Сейчас «Тумблер» не видит на этом компьютере ни одного работающего SOCKS5-прокси: VPN-клиент не запущен или не открывает локальный порт. Выбран порт {port}."
            : $"Сейчас на этом компьютере отвечают прокси на портах ниже. Нажми, чтобы выбрать; сейчас выбран порт {port}.";
    }

    void Topics_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        Body.DataContext = Topics.SelectedItem;
        ContentScroll.ScrollToHome();
    }

    void Port_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int port }) _usePort(port);
        ShowProxies();
    }

    void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
