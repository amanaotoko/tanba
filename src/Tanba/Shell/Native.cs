using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tanba.Shell;

/// <summary>
/// Win32. Жёсткие ссылки нужны ровно для одного: вытащить файл наружу
/// под нормальным именем. В хранилище он лежит как 000147__промо.mp4,
/// а перетащить надо промо.mp4, поэтому делаем ссылку во временную папку.
///
/// Здесь же номер файла: по нему файл узнаётся после того, как его
/// переложили или переименовали мимо программы.
/// </summary>
public static partial class Native
{
    // ── Номер файла ──────────────────────────────────────────────────────

    private const uint ShareAll = 0x00000007;          // чтение, запись, удаление
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;   // без этого не открыть папку
    private const int FileIdInfo = 18;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInfoEx(SafeFileHandle handle, int cls, IntPtr buffer, uint size);

    /// <summary>
    /// Номер файла на томе. Переживает и переименование, и перемещение
    /// в пределах тома, поэтому по нему переложенный файл узнаётся снова.
    ///
    /// Возвращается вместе с серийным номером тома: сам по себе номер файла
    /// уникален только внутри своего диска, и без тома два файла на разных
    /// дисках могли бы совпасть. За пределы тома он не переезжает вообще:
    /// после копирования на другой диск это уже другой файл с другим номером.
    ///
    /// Открываем с нулевым доступом: нам нужны только сведения о файле,
    /// а запрашивать чтение значило бы спотыкаться о занятые файлы.
    /// Возвращает null, если файловая система номеров не держит.
    /// </summary>
    public static string? FileId(string path)
    {
        using var h = CreateFile(Long(path), 0, ShareAll, IntPtr.Zero,
            OpenExisting, BackupSemantics, IntPtr.Zero);
        if (h.IsInvalid) return null;

        // FILE_ID_INFO: 8 байт серийного номера тома и 16 байт номера файла.
        var buf = Marshal.AllocHGlobal(24);
        try
        {
            if (!GetFileInfoEx(h, FileIdInfo, buf, 24)) return null;

            var volume = (ulong)Marshal.ReadInt64(buf);
            var id = new byte[16];
            Marshal.Copy(buf + 8, id, 0, 16);
            return volume.ToString("x16") + ":" + Convert.ToHexString(id);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ── Жёсткие ссылки ───────────────────────────────────────────────────

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string newFile, string existingFile, IntPtr attrs);

    /// <summary>Тот же файл под другим именем. Только внутри одного тома.</summary>
    public static void Link(string linkPath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        if (File.Exists(linkPath)) File.Delete(linkPath);

        if (!CreateHardLink(Long(linkPath), Long(targetPath), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"Не удалось создать жёсткую ссылку {linkPath}");
    }

    /// <summary>Снимает ограничение в 260 символов на длину пути.</summary>
    private static string Long(string p)
    {
        p = Path.GetFullPath(p);
        return p.StartsWith(@"\\?\") ? p : @"\\?\" + p;
    }

    public static string SafeName(string raw)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(raw.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray()).Trim();
        s = s.TrimEnd('.', ' ');
        return s.Length == 0 ? "_" : s;
    }
}
