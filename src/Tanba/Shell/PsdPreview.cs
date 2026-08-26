using System.Buffers.Binary;

namespace Tanba.Shell;

/// <summary>
/// Превью, которую Photoshop кладёт внутрь самого файла.
///
/// Нужна там, где оболочка отказалась, а отказывает она на больших макетах
/// закономерно. Эскизы для psd делает не Windows и не Photoshop, а сторонняя
/// надстройка, и внутри неё библиотека, которая не открывает файлы больше
/// 100 МиБ. Проверяет она размер файла, а не содержимое: та же картинка
/// 604x604, дописанная мусором до 105 МБ, эскиза уже не получает, а отказ
/// приходит за пару миллисекунд, то есть файл даже не читают. Выходит, что
/// без картинки остаются ровно самые тяжёлые макеты, ради которых каталог
/// и заводили.
///
/// Photoshop же кладёт готовую превьюшку в служебный блок 1036, и лежит он
/// в начале файла: читать все четыреста мегабайт незачем. Длинная сторона
/// у неё всегда 160 пикселей, чего хватает мелкой и средней плитке и с
/// натяжкой хватает крупной.
///
/// Полноразмерную склейку из конца файла мы намеренно не трогаем. Она там
/// есть и именно её читает надстройка, когда у неё получается, но стоит она
/// сотни миллисекунд и десятки мегабайт, а выигрыш виден только на самой
/// крупной плитке.
/// </summary>
public static class PsdPreview
{
    /// Служебные блоки лежат в начале, но длину они объявляют сами, и верить
    /// ей на слово нельзя: битый файл попросит гигабайт.
    private const int MaxResources = 64 * 1024 * 1024;

    /// Перед самим jpeg в блоке лежит его описание: формат, размеры, биты.
    private const int ThumbHeader = 28;

    public static bool Handles(string path) =>
        path.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".psb", StringComparison.OrdinalIgnoreCase);

    /// <summary>Готовый jpeg из файла или null. Ничего не декодирует.</summary>
    public static byte[]? Read(string path)
    {
        try
        {
            using var f = File.OpenRead(path);

            Span<byte> head = stackalloc byte[26];
            f.ReadExactly(head);
            if (!head[..4].SequenceEqual("8BPS"u8)) return null;

            // Цветовая таблица: нужна индексным и дуплексам, у остальных пуста.
            var palette = Be32(f);
            if (palette > 0) f.Seek(palette, SeekOrigin.Current);

            var len = Be32(f);
            if (len is 0 or > MaxResources) return null;

            var res = new byte[len];
            f.ReadExactly(res);
            return Find(res);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or EndOfStreamException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Ищет блок с превьюшкой среди служебных.</summary>
    private static byte[]? Find(byte[] res)
    {
        var i = 0;
        while (i + 12 <= res.Length)
        {
            // Блоки идут подряд и каждый начинается этой меткой. Не совпала
            // значит дальше не блоки, а что-то другое: разбор прекращаем.
            if (!res.AsSpan(i, 4).SequenceEqual("8BIM"u8)) return null;

            var id = BinaryPrimitives.ReadUInt16BigEndian(res.AsSpan(i + 4, 2));
            i += 6;

            // Имя: длина одним байтом, потом строка, и всё вместе до чётной длины.
            var name = res[i] + 1;
            i += name + (name % 2);
            if (i + 4 > res.Length) return null;

            var size = BinaryPrimitives.ReadUInt32BigEndian(res.AsSpan(i, 4));
            i += 4;
            if (size > int.MaxValue || i + (int)size > res.Length) return null;

            // 1036 это jpeg. 1033 то же самое от Photoshop 4, там в jpeg
            // переставлены местами красный и синий, но нам такие не встречались
            // и портить картинку хуже, чем не показать её вовсе.
            if (id == 1036 && size > ThumbHeader)
                return res[(i + ThumbHeader)..(i + (int)size)];

            i += (int)size + ((int)size % 2);
        }
        return null;
    }

    private static uint Be32(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }
}
