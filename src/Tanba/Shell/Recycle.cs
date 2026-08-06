using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Tanba.Web;

/// <summary>
/// Удаление в корзину Windows. Насовсем не стираем никогда: корзина
/// возвращает файл на прежнее место, а значит вместе с ним возвращаются
/// и теги, потому что строка в базе остаётся помеченной пропавшей.
/// </summary>
internal static class Recycle
{
    public static void Send(string path)
    {
        // Список путей заканчивается ДВУМЯ нулями: одинарный ноль отделяет
        // элементы, второй закрывает список. Без него SHFileOperation читает
        // за пределы строки.
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + '\0' + '\0',
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
        };

        var rc = SHFileOperation(ref op);
        if (rc != 0) throw new Win32Exception(rc, $"Не удалось отправить в корзину: {path}");
        if (op.fAnyOperationsAborted) throw new IOException($"Удаление отменено: {path}");
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;   // без этого флага файл стирается насовсем
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}
