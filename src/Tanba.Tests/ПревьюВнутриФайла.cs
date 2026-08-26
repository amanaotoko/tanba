using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using Tanba.Shell;

namespace Tanba.Tests;

/// <summary>
/// Большие макеты Photoshop оставались без картинки: эскизы для psd делает
/// сторонняя надстройка, а внутри неё библиотека, которая не открывает файлы
/// больше 100 МиБ. Проверяет она размер файла, а не содержимое, так что без
/// эскиза оставались ровно самые тяжёлые работы.
///
/// Выход был под рукой: Photoshop сам кладёт готовую превьюшку в служебный
/// блок в начале файла. Здесь проверяется, что мы её оттуда достаём, и что
/// на чужих или битых файлах разбор не падает, а просто отвечает «нет».
/// </summary>
public sealed class ПревьюВнутриФайла : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tanba-psd-").FullName;

    /// <summary>Настоящий маленький jpeg, чтобы проверять не только байты, но и картинку.</summary>
    private static byte[] Jpeg(int w, int h)
    {
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.SeaGreen);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }

    /// <summary>
    /// Собирает psd с одним служебным блоком. Слои и склейку не пишем: разбор
    /// до них не доходит и доходить не должен, в этом вся суть.
    /// </summary>
    private string Psd(string name, ushort resourceId, byte[]? jpeg, string signature = "8BPS")
    {
        var m = new MemoryStream();
        var w = new BinaryWriter(m);

        w.Write(System.Text.Encoding.ASCII.GetBytes(signature));
        Be16(w, 1);                        // версия
        w.Write(new byte[6]);              // резерв
        Be16(w, 3);                        // каналов
        Be32(w, 600); Be32(w, 800);        // высота, ширина
        Be16(w, 8); Be16(w, 3);            // бит на канал, режим RGB
        Be32(w, 0);                        // цветовой таблицы нет

        var res = new MemoryStream();
        var rw = new BinaryWriter(res);
        if (jpeg is not null)
        {
            rw.Write(System.Text.Encoding.ASCII.GetBytes("8BIM"));
            Be16(rw, resourceId);
            rw.Write((byte)0); rw.Write((byte)0);          // пустое имя, до чётной длины
            Be32(rw, (uint)(28 + jpeg.Length));            // описание плюс сам jpeg
            Be32(rw, 1); Be32(rw, 160); Be32(rw, 120);     // формат и размеры
            rw.Write(new byte[16]);                        // остаток описания
            rw.Write(jpeg);
            if (jpeg.Length % 2 != 0) rw.Write((byte)0);
        }

        Be32(w, (uint)res.Length);
        w.Write(res.ToArray());
        w.Flush();

        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, m.ToArray());
        return path;
    }

    private static void Be16(BinaryWriter w, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        w.Write(b);
    }

    private static void Be32(BinaryWriter w, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        w.Write(b);
    }

    [Fact]
    public void Достаёт_превью_и_она_остаётся_картинкой()
    {
        var jpeg = Jpeg(160, 120);
        var psd = Psd("макет.psd", 1036, jpeg);

        var got = PsdPreview.Read(psd);

        Assert.NotNull(got);
        Assert.Equal(jpeg, got);

        // Главное не байты, а то, что это по-прежнему открывается как картинка.
        using var ms = new MemoryStream(got!);
        using var bmp = new Bitmap(ms);
        Assert.Equal(160, bmp.Width);
        Assert.Equal(120, bmp.Height);
    }

    [Fact]
    public void Размер_файла_разбору_безразличен()
    {
        var jpeg = Jpeg(160, 120);
        var psd = Psd("тяжёлый.psd", 1036, jpeg);

        // Дописываем в хвост столько, что чужая библиотека отказалась бы:
        // её предел это размер файла, а не содержимое. Наш разбор читает
        // начало и хвоста не замечает.
        using (var f = File.Open(psd, FileMode.Append))
            f.Write(new byte[8 * 1024 * 1024]);

        Assert.Equal(jpeg, PsdPreview.Read(psd));
    }

    [Fact]
    public void Без_превью_и_на_чужом_файле_отвечает_нет()
    {
        Assert.Null(PsdPreview.Read(Psd("пустой.psd", 1036, null)));

        // Блок есть, но не тот: 1005 это разрешение печати.
        Assert.Null(PsdPreview.Read(Psd("другой блок.psd", 1005, Jpeg(8, 8))));

        // Вообще не psd.
        Assert.Null(PsdPreview.Read(Psd("чужой.psd", 1036, Jpeg(8, 8), signature: "RIFF")));

        // Обрубленный файл: заголовок обещает блоки, а их нет.
        var cut = Path.Combine(_dir, "обрубок.psd");
        File.WriteAllBytes(cut, File.ReadAllBytes(Psd("целый.psd", 1036, Jpeg(8, 8)))[..30]);
        Assert.Null(PsdPreview.Read(cut));
    }

    [Fact]
    public void Берётся_только_за_свои_расширения()
    {
        Assert.True(PsdPreview.Handles(@"S:\Inbox\макет.psd"));
        Assert.True(PsdPreview.Handles(@"S:\Inbox\МАКЕТ.PSB"));
        Assert.False(PsdPreview.Handles(@"S:\Inbox\фото.jpg"));
        Assert.False(PsdPreview.Handles(@"S:\Inbox\макет.cdr"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
