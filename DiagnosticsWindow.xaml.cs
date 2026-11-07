using System.Windows;
using System.Windows.Controls;

namespace Tumbler;

public partial class DiagnosticsWindow : Window
{
    readonly Func<DiagnosticsInput> _input;
    readonly Action _openLog;
    readonly Func<Window, bool>? _repair;
    List<CheckResult> _results = new();

    /// <param name="input">Снимок текущего состояния «Тумблера»; берётся заново при каждой проверке.</param>
    /// <param name="repair">Открывает мастер установки; null — чинить отсюда нельзя.</param>
    internal DiagnosticsWindow(Func<DiagnosticsInput> input, Action openLog, Func<Window, bool>? repair)
    {
        InitializeComponent();
        _input = input;
        _openLog = openLog;
        _repair = repair;
        SourceInitialized += (_, _) => Chrome.UseDarkTitleBar(this);
        Loaded += async (_, _) => await RunAsync();
    }

    /// <summary>Текстовый отчёт — его удобно вставить в чат, когда просишь помощи.</summary>
    internal static string Report(IEnumerable<CheckResult> results) => string.Join(Environment.NewLine, results.Select(r =>
    {
        var mark = r.State switch { CheckState.Ok => "[OK]", CheckState.Warning => "[!]", _ => "[X]" };
        var line = $"{mark} {r.Title}: {r.Detail}";
        return r.Command is null ? line : line + Environment.NewLine + "    " + r.Command;
    }));

    async Task RunAsync()
    {
        RunButton.IsEnabled = false;
        Summary.Text = "Проверяю…";
        var input = _input();
        _results = await Task.Run(() => Diagnostics.Run(input));
        Checks.ItemsSource = _results;

        int failed = _results.Count(r => r.State == CheckState.Fail);
        int warned = _results.Count(r => r.State == CheckState.Warning);
        Summary.Text = failed > 0 ? $"Найдено: {Text.Plural(failed, "проблема", "проблемы", "проблем")}"
            : warned > 0 ? $"Работает, но есть {Text.Plural(warned, "замечание", "замечания", "замечаний")}"
            : "Всё в порядке";
        Summary.Foreground = Chrome.Brush(failed > 0 ? "Danger" : warned > 0 ? "Warn" : "Accent");
        RepairButton.Visibility = _repair is not null && _results.Any(r => r.Repairable) ? Visibility.Visible : Visibility.Collapsed;
        RunButton.IsEnabled = true;
    }

    async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();

    async void Repair_Click(object sender, RoutedEventArgs e)
    {
        _repair?.Invoke(this);
        await RunAsync();
    }

    void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetText((string)((Button)sender).Tag);

    void CopyReport_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(Report(_results));

    void Log_Click(object sender, RoutedEventArgs e) => _openLog();

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
