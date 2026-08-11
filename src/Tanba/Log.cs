using System.Runtime.InteropServices;
using System.Text;

namespace Tanba;

/// <summary>
/// Журнал. Программа оконная, консоли у неё нет, а рассказать о себе ей есть
/// что: сканер, обход хранилища и перетаскивание сообщают об ошибках через
/// Console. Раньше это писалось в чёрное окно, которое открывалось рядом
/// с программой и мешало; теперь уходит в файл рядом с установленной копией.
///
/// Файл лежит в %LocalAppData%, а не на диске хранилища: диск может быть ещё
/// не подключён, а записать первые строки надо именно тогда.
/// </summary>
public static class Log
{
    private const int AttachParent = -1;
    private const long MaxBytes = 1 * 1024 * 1024;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    public static string Path { get; private set; } = "";

    public static void Start()
    {
        Path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tanba", "tanba.log");

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            // Одна прошлая копия про запас: журнал не должен расти вечно,
            // но и обрывать его на полуслове при разборе поломки нельзя.
            var info = new FileInfo(Path);
            if (info.Exists && info.Length > MaxBytes)
                File.Move(Path, Path + ".old", overwrite: true);

            var file = new StreamWriter(Path, append: true, Encoding.UTF8) { AutoFlush = true };

            // Если программу запустили из терминала, пишем и туда тоже:
            // ключ --no-window для того и существует, чтобы смотреть глазами.
            TextWriter? term = null;
            if (AttachConsole(AttachParent))
            {
                Console.OutputEncoding = Encoding.UTF8;
                term = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            }

            Console.SetOut(new Stamped(file, term));
            Console.SetError(new Stamped(file, term));

            Console.WriteLine($"--- запуск, {Update.Updater.Version} ---");
        }
        catch
        {
            // Без журнала жить можно, без программы нет.
        }
    }

    /// <summary>Ставит время в начале каждой строки и дублирует в терминал.</summary>
    private sealed class Stamped(TextWriter file, TextWriter? term) : TextWriter
    {
        private bool _fresh = true;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char c)
        {
            if (_fresh && c != '\n' && c != '\r')
            {
                var at = DateTime.Now.ToString("HH:mm:ss ");
                file.Write(at);
                term?.Write(at);
                _fresh = false;
            }
            if (c == '\n') _fresh = true;
            file.Write(c);
            term?.Write(c);
        }

        public override void Write(string? s)
        {
            if (s is null) return;
            foreach (var c in s) Write(c);
        }

        public override void Flush()
        {
            file.Flush();
            term?.Flush();
        }
    }
}
