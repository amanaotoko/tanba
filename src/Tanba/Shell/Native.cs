using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Tanba.Shell;

/// <summary>
/// Win32. Жёсткие ссылки нужны ровно для одного: вытащить файл наружу
/// под нормальным именем. В хранилище он лежит как 000147__промо.mp4,
/// а перетащить надо промо.mp4, поэтому делаем ссылку во временную папку.
/// </summary>
public static partial class Native
{
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
