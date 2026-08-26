using System.Runtime.InteropServices;

namespace Tanba.Host;

/// <summary>
/// Своя полоса заголовка, как в проводнике Windows 11: вкладки лежат прямо
/// в ней, кнопки окна справа.
///
/// Окно забираем у системы ЦЕЛИКОМ: WM_NCCALCSIZE отвечает нулём, и тогда
/// клиентская область совпадает с окном до последнего пикселя. Ни заголовка,
/// ни видимой рамки не остаётся, содержимое идёт до самого края. Так устроен
/// Chromium, то есть Brave, и так же выглядит проводник.
///
/// Плата за это одна: края окна теперь накрыты содержимым, и системе до них
/// не дотянуться. Растягивание возвращаем сами, см. BeginResize: страница
/// ловит край и просит Windows тянуть, дальше всё делает она.
///
/// Как делать НЕ надо, проверено: оставить системе кусок рамки и закрасить
/// его под цвет шапки. Полоса остаётся на месте, ест семь пикселей и видна.
/// </summary>
public sealed partial class MainWindow
{
    private const int WM_NCCALCSIZE = 0x0083;

    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CXPADDEDBORDER = 92;

    // Тёмный кадр и цвет кромки. Это не закрашивание дырки: кромка тут
    // толщиной в пиксель и места не занимает, просто иначе она светлая.
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;

    // Скругление углов, как у всех окон Windows 11. Обычным окнам система
    // скругляет углы сама, но наше забрало у неё всю неклиентскую область,
    // и такому она оставляет прямые углы. Просим явно, ровно так же это
    // делает Chromium, у которого окно устроено так же.
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    // Токен --strip, цвет полосы с вкладками: кромка идёт по её краю.
    private const int EdgeDark = 0x0C0B0B;   // #0B0B0C
    private const int EdgeLight = 0xEBEFEF;  // #EFEFEB

    /// <summary>На столько развёрнутое окно вылезает за рабочую область.</summary>
    private int Frame =>
        GetSystemMetricsForDpi(SM_CXSIZEFRAME, (uint)DeviceDpi) +
        GetSystemMetricsForDpi(SM_CXPADDEDBORDER, (uint)DeviceDpi);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            // Ноль значит «клиентская область равна предложенной», а предложена
            // тут вся площадь окна. Ничего не вычитаем: именно вычитание
            // разъезжается с тем, как размеры считает WinForms.
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// Развёрнутое окно Windows нарочно делает больше рабочей области на
    /// толщину рамки, чтобы спрятать её за краями экрана. Клиент теперь равен
    /// окну, поэтому без отступа за экран уехало бы и содержимое.
    /// </summary>
    private void FitFrame()
    {
        var pad = WindowState == FormWindowState.Maximized ? Frame : 0;
        if (Padding.All != pad) Padding = new Padding(pad);
    }

    /// <summary>Границы окна на момент, когда взялись за край.</summary>
    private Rectangle _resizeFrom;

    /// <summary>
    /// Взялись за край. Дальше перетаскивание ведёт сама страница и присылает
    /// смещение, а мы двигаем границы.
    ///
    /// Штатный способ, WM_NCLBUTTONDOWN, здесь не работает: мышь в этот момент
    /// захвачена процессом WebView2, а не нашим, и системный цикл изменения
    /// размера просто не получает ни движений, ни отпускания кнопки.
    /// У страницы же мышь никто не отбирает.
    /// </summary>
    private void BeginResize() => _resizeFrom = Bounds;

    private void ApplyResize(string? edge, int dx, int dy)
    {
        if (edge is null || WindowState == FormWindowState.Maximized) return;

        var (l, t) = (_resizeFrom.Left, _resizeFrom.Top);
        var (r, b) = (_resizeFrom.Right, _resizeFrom.Bottom);

        if (edge.Contains('l')) l += dx;
        if (edge.Contains('r')) r += dx;
        if (edge.Contains('t')) t += dy;
        if (edge.Contains('b')) b += dy;

        // Меньше минимума не даём: иначе край проскакивает насквозь
        // и окно начинает выворачиваться наизнанку.
        if (r - l < MinimumSize.Width)
        {
            if (edge.Contains('l')) l = r - MinimumSize.Width; else r = l + MinimumSize.Width;
        }
        if (b - t < MinimumSize.Height)
        {
            if (edge.Contains('t')) t = b - MinimumSize.Height; else b = t + MinimumSize.Height;
        }

        Bounds = new Rectangle(l, t, r - l, b - t);
    }

    /// <summary>Красит кромку в пиксель толщиной, которой распоряжается система.</summary>
    private void PaintFrame(bool light)
    {
        if (!IsHandleCreated) return;

        var dark = light ? 0 : 1;
        var edge = light ? EdgeLight : EdgeDark;

        // Старые сборки Windows этих свойств не знают и вернут ошибку.
        // Ничего не сломается, кромка останется системной, углы прямыми.
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref edge, sizeof(int));

        // К теме отношения не имеет, но живёт на той же рамке и точно так же
        // слетает, если окно пересоздадут, поэтому ставится здесь.
        var round = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
