using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Tumbler.Daemon;

namespace Tumbler.Gui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = GuiOptions.Parse(desktop.Args ?? []);
            IBackend backend = options.Demo ? new DemoBackend() : new LinuxBackend();
            // Пока открыт мастер установки, главного окна ещё нет — завершаемся только явно.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = StartAsync(desktop, backend, options);
        }
        base.OnFrameworkInitializationCompleted();
    }

    static async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, IBackend backend, GuiOptions options)
    {
        if (options.Dialog == "setup")
        {
            await Shots.CaptureAsync(new SetupWindow(backend), options.Screenshot!);
            desktop.Shutdown();
            return;
        }

        // Служба не отвечает — значит, «Тумблер» ещё не установлен на этом компьютере (или служба упала).
        if (!backend.IsDemo && await backend.CallAsync(new Request()) is null)
        {
            var setup = new SetupWindow(backend);
            desktop.MainWindow = setup;
            setup.Show();
            if (!await setup.Completion)
            {
                desktop.Shutdown();
                return;
            }
            setup.Close();
        }

        var main = new MainWindow(backend, options);
        desktop.MainWindow = main;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        if (!options.Tray) main.Show();
    }

    public static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://tumbler/Assets/tumbler.png"));
        return new WindowIcon(new Bitmap(stream));
    }
}

/// <summary>Ключи окна. Всё, кроме --tray, нужно для отладки и снимков экрана.</summary>
public sealed class GuiOptions
{
    /// <summary>Стартовать свёрнутым в трей.</summary>
    public bool Tray { get; private set; }

    /// <summary>Демонстрационные данные вместо системы и службы.</summary>
    public bool Demo { get; private set; }

    /// <summary>Сохранить снимок окна в PNG и выйти.</summary>
    public string? Screenshot { get; private set; }

    /// <summary>Снимать диалог: setup, settings, diagnostics, help или help:НОМЕР.</summary>
    public string? Dialog { get; private set; }

    public static GuiOptions Parse(string[] args)
    {
        var options = new GuiOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--tray": options.Tray = true; break;
                case "--demo": options.Demo = true; break;
                case "--screenshot": options.Screenshot = Next(); break;
                case "--dialog": options.Dialog = Next(); break;
            }
        }
        return options;
    }
}

/// <summary>Снимки окон для проверки вёрстки: окно открывается за краем экрана и не забирает фокус.</summary>
internal static class Shots
{
    public static void MoveOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(-30000, -30000);
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
    }

    public static async Task CaptureAsync(Window window, string file, int delayMs = 1200)
    {
        MoveOffscreen(window);
        window.Show();
        await Task.Delay(delayMs);
        Save(window, file);
        window.Close();
    }

    public static void Save(Window window, string file)
    {
        double scale = window.RenderScaling;
        var size = new PixelSize((int)(window.Bounds.Width * scale), (int)(window.Bounds.Height * scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        bitmap.Save(file);
    }
}
