using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Tanba.Shell;

/// <summary>
/// Эскизы просим у оболочки Windows. На машине с CorelDRAW и Adobe уже
/// зарегистрированы обработчики эскизов для cdr, ai, psd: те самые, которыми
/// проводник рисует превью. Разбирать форматы самим смысла нет: через оболочку
/// покрытие шире любой библиотеки и растёт вместе с установленным софтом.
///
/// Результат кладём в cfg.Thumbs как jpg. Кэш свежее файла, значит отдаём его.
/// </summary>
public sealed partial class Thumbs(Config cfg)
{
    public const int DefaultSize = 256;

    /// Обработчик эскизов может уйти в себя надолго. Ждём и бросаем.
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// Сколько не трогать файл, для которого оболочка ничего не дала.
    private static readonly TimeSpan FailCooldown = TimeSpan.FromMinutes(5);

    /// Один эскиз, одна работа. Два запроса на ту же карточку не должны
    /// одновременно дёргать оболочку и писать в один и тот же файл.
    private readonly ConcurrentDictionary<string, object> _locks = new();

    /// Отказы помним недолго: у части файлов эскиза нет вообще, и ходить
    /// за ним при каждой прокрутке списка незачем.
    private readonly ConcurrentDictionary<string, long> _failedUntil = new();

    /// <summary>
    /// Путь к готовому jpg или null, если эскиз получить не удалось.
    /// Вызов блокирующий: оболочка работает синхронно.
    /// </summary>
    public string? GetOrCreate(long fileId, string fullPath, int size = DefaultSize)
    {
        size = Math.Clamp(size, 16, 1024);
        var cache = CachePath(fileId, size);
        var src = new FileInfo(fullPath);

        if (IsFresh(cache, src)) return cache;

        // Исходник пропал, но прежний эскиз всё равно лучше пустого места.
        if (!src.Exists) return File.Exists(cache) ? cache : null;

        if (_failedUntil.TryGetValue(cache, out var until) && Environment.TickCount64 < until)
            return null;

        lock (_locks.GetOrAdd(cache, _ => new object()))
        {
            // Пока стояли в очереди, сосед мог всё сделать за нас.
            if (IsFresh(cache, src)) return cache;

            using var bmp = Extract(src.FullName, size);
            if (bmp is null)
            {
                _failedUntil[cache] = Environment.TickCount64 + (long)FailCooldown.TotalMilliseconds;
                return null;
            }

            try { SaveJpeg(bmp, cache); }
            catch (Exception e) when (e is IOException or ExternalException or UnauthorizedAccessException)
            {
                return null;
            }

            _failedUntil.TryRemove(cache, out _);
            return cache;
        }
    }

    /// <summary>Выбросить эскизы файла: содержимое изменилось или файл удалён.</summary>
    public void Forget(long fileId)
    {
        var dir = ShardDir(fileId);
        if (!Directory.Exists(dir)) return;

        foreach (var p in Directory.EnumerateFiles(dir, $"{fileId:D6}*.jpg"))
        {
            try { File.Delete(p); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            _failedUntil.TryRemove(p, out _);
        }
    }

    // ── кэш на диске ─────────────────────────────────────────────────────

    /// Шардинг по тысяче: в одной папке не окажется ста тысяч файлов.
    private string ShardDir(long fileId) => Path.Combine(cfg.Thumbs, $"{fileId / 1000:D3}");

    /// Обычный размер живёт под коротким именем, прочие получают суффикс.
    private string CachePath(long fileId, int size) => Path.Combine(ShardDir(fileId),
        size == DefaultSize ? $"{fileId:D6}.jpg" : $"{fileId:D6}_{size}.jpg");

    private static bool IsFresh(string cache, FileInfo src)
    {
        var t = new FileInfo(cache);
        return t.Exists && t.Length > 0 && src.Exists && t.LastWriteTimeUtc >= src.LastWriteTimeUtc;
    }

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>Пишем через временное имя: недописанный jpg не должен попасть в кэш.</summary>
    private static void SaveJpeg(Bitmap bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = $"{path}.{Environment.CurrentManagedThreadId}.tmp";

        try
        {
            using (var pars = new EncoderParameters(1))
            {
                pars.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
                bmp.Save(tmp, JpegCodec, pars);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            // Дошли сюда с живым tmp, значит сорвались. Обломок за собой убираем.
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); }
                catch (IOException) { }
            }
        }
    }

    // ── оболочка ─────────────────────────────────────────────────────────

    /// Порядок отката: настоящий эскиз, потом что дадут, потом хотя бы значок.
    private static readonly SIIGBF[] Attempts =
        [SIIGBF.ThumbnailOnly, SIIGBF.ResizeToFit, SIIGBF.IconOnly];

    private static Bitmap? Extract(string path, int size) => RunSta(() =>
    {
        var iid = IidShellItemImageFactory;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) < 0 || factory is null)
            return null;

        try
        {
            foreach (var flags in Attempts)
            {
                var hbmp = IntPtr.Zero;
                try
                {
                    factory.GetImage(new SIZE(size, size), flags, out hbmp);
                    if (hbmp == IntPtr.Zero) continue;
                    var bmp = ToBitmap(hbmp);
                    if (bmp is not null) return bmp;
                }
                catch (COMException) { /* нет обработчика или он отказал, пробуем следующий флаг */ }
                finally { if (hbmp != IntPtr.Zero) DeleteObject(hbmp); }
            }
            return null;
        }
        finally { Marshal.FinalReleaseComObject(factory); }
    });

    /// <summary>
    /// HBITMAP → Bitmap. Эскиз приходит 32-битным с премультиплицированной альфой,
    /// а Image.FromHbitmap альфу теряет, и прозрачный фон логотипа становится чёрным.
    /// Поэтому кладём эскиз на белое через AlphaBlend: jpeg прозрачность всё равно
    /// не хранит, а GDI сам разберётся и с премультипликацией, и с порядком строк
    /// в DIB: по заголовку его не угадать, оболочка отдаёт строки сверху вниз
    /// при положительном biHeight.
    /// </summary>
    private static Bitmap? ToBitmap(IntPtr hbmp)
    {
        var bm = new BITMAP();
        if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.Width <= 0 || bm.Height <= 0)
            return null;

        try
        {
            if (bm.BitsPixel != 32 || !HasAlpha(bm)) return Image.FromHbitmap(hbmp);

            var flat = new Bitmap(bm.Width, bm.Height, PixelFormat.Format24bppRgb);
            var ok = false;

            using (var g = Graphics.FromImage(flat))
            {
                g.Clear(Color.White);
                var dst = g.GetHdc();
                var src = CreateCompatibleDC(IntPtr.Zero);
                var prev = SelectObject(src, hbmp);
                try
                {
                    var blend = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
                    ok = AlphaBlend(dst, 0, 0, bm.Width, bm.Height, src, 0, 0, bm.Width, bm.Height, blend);
                }
                finally
                {
                    SelectObject(src, prev);
                    DeleteDC(src);
                    g.ReleaseHdc(dst);
                }
            }

            if (ok) return flat;

            // Смешать не вышло, берём без альфы, это лучше пустого места.
            flat.Dispose();
            return Image.FromHbitmap(hbmp);
        }
        catch (Exception e) when (e is ArgumentException or ExternalException) { return null; }
    }

    /// <summary>
    /// Есть ли в эскизе осмысленная альфа. Часть обработчиков отдаёт канал
    /// забитым нулями, смешивание такого дало бы чистый белый лист.
    /// Порядок строк тут не важен: ищем хоть один непрозрачный пиксель.
    /// </summary>
    private static bool HasAlpha(in BITMAP bm)
    {
        if (bm.Bits == IntPtr.Zero) return false;
        for (var y = 0; y < bm.Height; y++)
        {
            var row = IntPtr.Add(bm.Bits, y * bm.WidthBytes);
            for (var x = 3; x < bm.Width * 4; x += 4)
                if (Marshal.ReadByte(row, x) != 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Обработчики эскизов пишут под проводник, а он зовёт их из STA.
    /// Из потока пула (MTA) часть из них просто отказывает, поэтому
    /// каждое извлечение уносим в свой однопоточный апартамент.
    /// </summary>
    private static Bitmap? RunSta(Func<Bitmap?> work)
    {
        Bitmap? result = null;
        var t = new Thread(() =>
        {
            // Ловим всё: в этом потоке крутится чужой код, а необработанное
            // исключение в фоновом потоке уронило бы весь процесс.
            try { result = work(); }
            catch (Exception) { result = null; }
        }) { IsBackground = true };

        t.SetApartmentState(ApartmentState.STA);
        t.Start();

        // Зависший обработчик остаётся висеть в фоновом потоке, ждать его нечего.
        return t.Join(Deadline) ? result : null;
    }

    // ── COM и Win32 ──────────────────────────────────────────────────────

    private static readonly Guid IidShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    /// <summary>
    /// У IShellItemImageFactory ровно один собственный метод. В vtable перед ним
    /// только IUnknown, объявлять его здесь не нужно и нельзя.
    /// </summary>
    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,   // вписать в запрошенный размер
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,      // значок типа файла, последняя надежда
        ThumbnailOnly = 0x08, // только настоящий эскиз, иначе отказ
        InCacheOnly = 0x10,
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SIZE(int cx, int cy)
    {
        public readonly int Cx = cx;
        public readonly int Cy = cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }

    private const byte AcSrcAlpha = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;              // 0 = AC_SRC_OVER, других и нет
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    // Генератору LibraryImport COM-интерфейс в out-параметре не по зубам,
    // поэтому здесь классический DllImport.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindCtx,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static partial int GetObject(IntPtr handle, int size, ref BITMAP target);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport("msimg32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AlphaBlend(
        IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, BLENDFUNCTION blend);
}
