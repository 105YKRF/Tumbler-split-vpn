using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace Tumbler;

internal enum TrayState { Active, Paused, Warning, Problem }

/// <summary>Значок в области уведомлений: состояние цветом, пауза и профили в меню.</summary>
internal sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon _icon = new() { Visible = true };
    readonly Dictionary<TrayState, System.Drawing.Icon> _icons = new();

    TrayState _state = TrayState.Paused;
    bool _enabled;
    string _activeProfile = "";
    IReadOnlyList<string> _profiles = [];

    public Tray()
    {
        _icons[TrayState.Active] = Draw("#4ADE80", thumbRight: true);
        _icons[TrayState.Paused] = Draw("#8B93A1", thumbRight: false);
        _icons[TrayState.Warning] = Draw("#F5B94A", thumbRight: true);
        _icons[TrayState.Problem] = Draw("#F87171", thumbRight: true);

        _icon.Icon = _icons[_state];
        _icon.Text = "Тумблер";
        _icon.ContextMenuStrip = new Forms.ContextMenuStrip();
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke();
        };
    }

    public event Action? OpenRequested;
    public event Action? PauseToggled;
    public event Action<string>? ProfileSelected;
    public event Action? ExitRequested;

    public void Update(TrayState state, string tooltip, bool enabled, IReadOnlyList<string> profiles, string activeProfile)
    {
        if (state != _state) _icon.Icon = _icons[_state = state];
        // Подсказка значка в Windows ограничена 127 символами.
        _icon.Text = tooltip.Length > 120 ? tooltip[..120] : tooltip;
        _enabled = enabled;
        _profiles = profiles;
        _activeProfile = activeProfile;
    }

    public void Notify(string text) => _icon.ShowBalloonTip(3000, "Тумблер", text, Forms.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
    }

    void BuildMenu()
    {
        var items = _icon.ContextMenuStrip!.Items;
        items.Clear();

        items.Add("Открыть Тумблер", null, (_, _) => OpenRequested?.Invoke()).Font =
            new System.Drawing.Font(_icon.ContextMenuStrip.Font, System.Drawing.FontStyle.Bold);
        items.Add(_enabled ? "Поставить на паузу" : "Снять с паузы", null, (_, _) => PauseToggled?.Invoke());

        if (_profiles.Count > 1)
        {
            items.Add(new Forms.ToolStripSeparator());
            foreach (var name in _profiles)
            {
                var item = new Forms.ToolStripMenuItem($"Профиль «{name}»") { Checked = name == _activeProfile };
                item.Click += (_, _) => ProfileSelected?.Invoke(name);
                items.Add(item);
            }
        }

        items.Add(new Forms.ToolStripSeparator());
        items.Add("Выход", null, (_, _) => ExitRequested?.Invoke());
    }

    /// <summary>Рисует значок-переключатель нужного цвета; на паузе ползунок слева.</summary>
    static System.Drawing.Icon Draw(string color, bool thumbRight)
    {
        var fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(fill, null, new Rect(1, 8, 30, 16), 8, 8);
            dc.DrawEllipse(Brushes.White, null, new Point(thumbRight ? 23 : 9, 16), 5.5, 5.5);
        }
        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;

        using var gdi = new System.Drawing.Bitmap(stream);
        var handle = gdi.GetHicon();
        try
        {
            // Клонируем, чтобы значок не зависел от дескриптора, который тут же освобождаем.
            using var temporary = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)temporary.Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }
}
