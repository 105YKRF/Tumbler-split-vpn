using System.Windows;
using System.Windows.Input;

namespace Tumbler;

/// <summary>Маленький диалог «введи строку» с проверкой значения.</summary>
public partial class PromptWindow : Window
{
    readonly Func<string, string?> _validate;

    PromptWindow(string question, string initial, Func<string, string?> validate)
    {
        InitializeComponent();
        _validate = validate;
        Question.Text = question;
        Input.Text = initial;
        SourceInitialized += (_, _) => Chrome.UseDarkTitleBar(this);
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    /// <param name="validate">Возвращает текст ошибки или null, если значение подходит.</param>
    /// <returns>Введённая строка или null, если пользователь отказался.</returns>
    public static string? Ask(Window owner, string question, string initial, Func<string, string?> validate)
    {
        var dialog = new PromptWindow(question, initial, validate) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Input.Text.Trim() : null;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_validate(Input.Text.Trim()) is { } error)
        {
            Error.Text = error;
            Error.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }

    void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Ok_Click(sender, e);
    }
}
