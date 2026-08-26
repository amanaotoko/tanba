using Microsoft.Data.Sqlite;

namespace Tanba;

/// <summary>
/// Где лежит хранилище и что уже есть в выбранной папке.
///
/// Сам путь нельзя держать в базе: база лежит внутри хранилища, и программа
/// не может прочитать настройку, которая говорит, где эта настройка. Поэтому
/// он живёт отдельным файлом рядом с установленной копией, там же, где журнал.
/// Обновление подменяет только папку current, а этот файл лежит выше и
/// переживает её; удаление программы уносит его вместе с папкой, и правильно.
/// </summary>
public static class RootStore
{
    /// <summary>
    /// Куда писать настройку. Подменяется только тестами: настоящая программа
    /// всегда кладёт её рядом с установленной копией, и трогать это из кода
    /// незачем. Шов нужен потому, что забытый выбор уже один раз доехал до
    /// человека, и проверять его на настоящем %LocalAppData% нельзя, там
    /// лежит его собственная настройка.
    /// </summary>
    public static string? HomeForTests;

    public static string SettingPath => Path.Combine(
        HomeForTests ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Tanba", "root.txt");

    public static string? Read()
    {
        try
        {
            if (!File.Exists(SettingPath)) return null;
            var line = File.ReadAllText(SettingPath).Trim();
            return string.IsNullOrWhiteSpace(line) ? null : line;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Запоминает выбор. Не бросает: не сумели записать это неприятно, но
    /// не повод не дать человеку работать. Зато говорим об этом в журнал,
    /// иначе программа молча спрашивала бы место при каждом запуске.
    /// </summary>
    public static void Write(string root)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingPath)!);
            File.WriteAllText(SettingPath, Path.GetFullPath(root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Не смог запомнить хранилище: {ex.Message}");
        }
    }

    // ── Что в папке ──────────────────────────────────────────────────────

    public enum Kind
    {
        /// Пусто или папки ещё нет: заведём всё с нуля.
        Empty,
        /// Уже наш каталог: подключаемся и ничего не трогаем.
        Tanba,
        /// Чужое добро: свои папки создадим рядом, но предупредим.
        Foreign,
        /// Писать сюда нельзя, выбирать нечего.
        Bad,
    }

    /// <param name="Files">Сколько файлов в найденном каталоге, если он найден.</param>
    /// <param name="Items">Сколько посторонних записей в папке.</param>
    public sealed record Look(Kind Kind, string Path, long Files, int Items, string Note);

    public static Look Inspect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new Look(Kind.Bad, "", 0, 0, "Путь не указан.");

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { return new Look(Kind.Bad, path, 0, 0, "Такого пути не бывает."); }

        // Папки может ещё не быть: это нормально, создадим, если родитель
        // на месте и пишется. Проверять надо именно родителя, а не саму папку.
        if (!Directory.Exists(full))
        {
            var parent = Path.GetDirectoryName(full);
            if (parent is null || !Directory.Exists(parent))
                return new Look(Kind.Bad, full, 0, 0, "Такой папки нет, и создать её негде.");
            if (!Writable(parent))
                return new Look(Kind.Bad, full, 0, 0, "В это место нельзя писать.");
            return new Look(Kind.Empty, full, 0, 0, "Папки пока нет, создам её.");
        }

        if (!Writable(full))
            return new Look(Kind.Bad, full, 0, 0, "В эту папку нельзя писать.");

        var db = Path.Combine(full, "_meta", "tanba.db");
        if (File.Exists(db))
        {
            var n = CountFiles(db);
            return new Look(Kind.Tanba, full, n, 0,
                n >= 0 ? $"Здесь уже есть каталог Tanba, в нём {n} {Plural(n)}. Подключусь к нему."
                       : "Здесь уже есть каталог Tanba, но прочитать его не вышло.");
        }

        int items;
        try { items = Directory.EnumerateFileSystemEntries(full).Take(50).Count(); }
        catch (Exception) { return new Look(Kind.Bad, full, 0, 0, "Папку не прочитать."); }

        return items == 0
            ? new Look(Kind.Empty, full, 0, 0, "Папка пуста, заведу всё с нуля.")
            : new Look(Kind.Foreign, full, 0, items,
                "Папка не пуста. Свои папки я создам рядом с тем, что там лежит, " +
                "но лучше выбрать пустое место или отдельный диск.");
    }

    /// <summary>Ищет уже существующие каталоги на подключённых дисках.</summary>
    public static List<string> Suggest()
    {
        var found = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                if (File.Exists(Path.Combine(d.RootDirectory.FullName, "_meta", "tanba.db")))
                    found.Add(d.RootDirectory.FullName);
            }
            catch (Exception) { /* диск отвалился прямо сейчас, пропускаем */ }
        }
        return found;
    }

    private static bool Writable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".tanba-probe");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Сколько файлов в чужом каталоге. Только чтение, база не трогается.</summary>
    private static long CountFiles(string dbPath)
    {
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();

            using var c = new SqliteConnection(cs);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM files WHERE is_missing = 0";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        catch (Exception) { return -1; }
    }

    private static string Plural(long n)
    {
        var a = Math.Abs(n) % 100;
        var b = a % 10;
        if (a > 10 && a < 20) return "файлов";
        if (b > 1 && b < 5) return "файла";
        if (b == 1) return "файл";
        return "файлов";
    }
}
