using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Tumbler.Gui;

/// <summary>
/// Простые диалоги: в Avalonia нет MessageBox. Окна строятся кодом — для трёх кнопок отдельная разметка не нужна.
/// </summary>
internal static class Dialogs
{
    /// <returns>true — нажата кнопка подтверждения.</returns>
    public static async Task<bool> Confirm(Window owner, string text, string ok = "Да", string cancel = "Отмена")
    {
        var window = Build(owner, text, out var buttons);
        bool result = false;
        buttons.Children.Add(MakeButton(cancel, false, () => window.Close()));
        buttons.Children.Add(MakeButton(ok, true, () =>
        {
            result = true;
            window.Close();
        }));
        await window.ShowDialog(owner);
        return result;
    }

    public static async Task Info(Window owner, string text)
    {
        var window = Build(owner, text, out var buttons);
        buttons.Children.Add(MakeButton("Понятно", true, () => window.Close()));
        await window.ShowDialog(owner);
    }

    /// <param name="validate">Возвращает текст ошибки или null, если значение подходит.</param>
    /// <returns>Введённая строка или null, если пользователь отказался.</returns>
    public static async Task<string?> Ask(Window owner, string question, string initial, Func<string, string?> validate)
    {
        var window = Build(owner, question, out var buttons);
        var input = new TextBox { Text = initial, Margin = new Thickness(0, 12, 0, 0) };
        var error = new TextBlock { Foreground = (IBrush)Application.Current!.FindResource("Danger")!, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), IsVisible = false, TextWrapping = TextWrapping.Wrap };
        var body = (StackPanel)window.Content!;
        body.Children.Insert(1, input);
        body.Children.Insert(2, error);

        string? result = null;
        void Submit()
        {
            var value = input.Text?.Trim() ?? "";
            if (validate(value) is { } problem)
            {
                error.Text = problem;
                error.IsVisible = true;
                return;
            }
            result = value;
            window.Close();
        }

        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Submit();
        };
        buttons.Children.Add(MakeButton("Отмена", false, () => window.Close()));
        buttons.Children.Add(MakeButton("Готово", true, Submit));
        window.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        await window.ShowDialog(owner);
        return result;
    }

    static Window Build(Window owner, string text, out StackPanel buttons)
    {
        buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 18, 0, 0) };
        var body = new StackPanel { Margin = new Thickness(22, 20, 22, 16) };
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(buttons);
        return new Window
        {
            Title = "Тумблер",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = owner.Icon,
            Content = body,
        };
    }

    static Button MakeButton(string text, bool accent, Action click)
    {
        var button = new Button { Content = text, MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (accent) button.Classes.Add("accent");
        button.Click += (_, _) => click();
        return button;
    }
}
