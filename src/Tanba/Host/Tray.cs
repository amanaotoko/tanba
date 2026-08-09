using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Tanba.Host;

/// <summary>
/// Значок в трее со счётчиком непрочитанного разбора.
/// Не всплывашка на каждый файл: та задолбает за неделю и её выключат.
/// Тихая цифра, которая мозолит глаз, работает годами.
/// </summary>
public sealed partial class Tray : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Action _open;
    private readonly Action _rescan;
    private Icon? _current;
    private int _lastNotified = -1;

    public Tray(Action open, Action rescan, Action quit)
    {
        _open = open;
        _rescan = rescan;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть Tanba", null, (_, _) => _open());
        menu.Items.Add("Пересканировать приём", null, (_, _) => _rescan());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => quit());

        _icon = new NotifyIcon
        {
            Visible = true,
            Text = "Tanba",
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => _open();

        Update(0);
    }

    /// <summary>Обновляет цифру. Уведомление показываем только когда счётчик вырос заметно.</summary>
    public void Update(int pending)
    {
        var old = _current;
        _current = Render(pending);
        _icon.Icon = _current;
        old?.Dispose();

        _icon.Text = pending == 0
            ? "Tanba, всё разобрано"
            : $"Tanba, {pending} {Plural(pending, "файл ждёт", "файла ждут", "файлов ждут")} разбора";

        // Одно ненавязчивое уведомление на каждую новую десятку, а не на каждый файл.
        var bucket = pending / 10;
        if (pending > 0 && bucket > 0 && bucket != _lastNotified)
        {
            _lastNotified = bucket;
            _icon.ShowBalloonTip(4000, "Tanba",
                $"В «СОХРАНИ СЮДА» накопилось {pending}. Разберёшь одной пачкой.",
                ToolTipIcon.None);
        }
        if (pending == 0) _lastNotified = -1;
    }

    /// <summary>Разовая подсказка от значка. Для редких случаев, не для потока событий.</summary>
    public void Say(string text)
    {
        try { _icon.ShowBalloonTip(5000, "Tanba", text, ToolTipIcon.None); }
        catch (InvalidOperationException) { /* значок уже снят */ }
    }

    /// <summary>Рисуем значок сами: цифра важнее картинки, а .ico пришлось бы плодить на каждый случай.</summary>
    private static Icon Render(int count)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);

            using var back = new SolidBrush(count > 0
                ? ColorTranslator.FromHtml("#DFF56E")     // лайм, то же главное действие
                : ColorTranslator.FromHtml("#2E2E33"));
            using var path = Rounded(new Rectangle(1, 1, 30, 30), 8);
            g.FillPath(back, path);

            var fg = count > 0 ? ColorTranslator.FromHtml("#0E0E10") : ColorTranslator.FromHtml("#9E9EA6");
            var text = count == 0 ? "T" : count > 99 ? "99+" : count.ToString();
            var size = text.Length >= 3 ? 12f : text.Length == 2 ? 15f : 17f;

            using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(fg);
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString(text, font, brush, new RectangleF(0, 0, 32, 33), fmt);
        }

        // GetHicon отдаёт неуправляемый хендл, копируем в управляемый и сразу освобождаем.
        var h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally { DestroyIcon(h); }
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static string Plural(int n, string one, string few, string many)
    {
        var a = Math.Abs(n) % 100;
        var b = a % 10;
        if (a is > 10 and < 20) return many;
        return b switch { 1 => one, > 1 and < 5 => few, _ => many };
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
