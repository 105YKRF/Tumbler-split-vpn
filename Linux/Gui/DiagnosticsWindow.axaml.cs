using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Tumbler.Gui;

public partial class DiagnosticsWindow : Window
{
    readonly Func<DiagnosticsInput> _input;
    readonly IBackend _backend;
    List<CheckResult> _results = new();

    public DiagnosticsWindow() : this(() => new DiagnosticsInput(new Dictionary<int, int>(), false, []), new DemoBackend())
    {
    }

    /// <param name="input">Снимок выбранного в окне; берётся заново при каждой проверке.</param>
    internal DiagnosticsWindow(Func<DiagnosticsInput> input, IBackend backend)
    {
        InitializeComponent();
        _input = input;
        _backend = backend;
        Icon = App.LoadIcon();
        Opened += async (_, _) => await RunAsync();
    }

    async Task RunAsync()
    {
        RunButton.IsEnabled = false;
        Summary.Text = "Проверяю…";
        _results = await Diagnostics.RunAsync(_backend, _input());
        Checks.ItemsSource = _results;

        int failed = _results.Count(r => r.State == CheckState.Fail);
        int warned = _results.Count(r => r.State == CheckState.Warning);
        Summary.Text = failed > 0 ? $"Найдено: {Text.Plural(failed, "проблема", "проблемы", "проблем")}"
            : warned > 0 ? $"Работает, но есть {Text.Plural(warned, "замечание", "замечания", "замечаний")}"
            : "Всё в порядке";
        Summary.Foreground = (IBrush)this.FindResource(failed > 0 ? "Danger" : warned > 0 ? "Warn" : "Accent")!;
        RunButton.IsEnabled = true;
    }

    async void Run_Click(object? sender, RoutedEventArgs e) => await RunAsync();

    async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string command } && Clipboard is { } clipboard) await clipboard.SetTextAsync(command);
    }

    async void CopyReport_Click(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(Diagnostics.Report(_results));
    }

    void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
