namespace Tanba;

/// <summary>
/// Раскладка хранилища. Всё считается от корня диска.
/// Переопределяется переменной окружения TANBA_ROOT.
/// </summary>
public sealed class Config
{
    public const string InboxName = "Inbox";

    /// <summary>
    /// Как эта папка называлась раньше. Имя кричало капсом, чтобы его нашли
    /// среди прочих, но прочие скрыты, и в диалоге сохранения она всё равно
    /// одна. Держим только ради переноса, см. RenameOldInbox.
    /// </summary>
    public const string OldInboxName = "СОХРАНИ СЮДА";

    public string Root { get; }

    public Config(string root) => Root = Path.GetFullPath(root);

    /// Единственная папка, куда сохраняют люди.
    public string Inbox => Path.Combine(Root, InboxName);

    /// Реальные байты. Только программа пишет сюда.
    /// Папок по категориям на диске нет: раскладку показывает программа.
    public string Store => Path.Combine(Root, "_store");

    public string Versions => Path.Combine(Root, "_versions");
    public string Thumbs => Path.Combine(Root, "_thumbs");
    public string Meta => Path.Combine(Root, "_meta");

    /// Отсеянный мусор: Thumbs.db, автосохранения и прочее. Ничего не удаляем, только откладываем.
    public string Junk => Path.Combine(Meta, "junk");

    /// Резервные копии Corel и прочее, что станет версиями файлов.
    public string VersionsInbox => Path.Combine(Meta, "versions-inbox");

    public string DbPath => Path.Combine(Meta, "tanba.db");
    public string CatalogCsv => Path.Combine(Meta, "catalog.csv");
    public string BackupDir => Path.Combine(Meta, "backup");

    /// Служебные папки: сканер никогда не считает их содержимым каталога.
    public string[] ServiceDirs => [Store, Versions, Thumbs, Meta];

    /// <summary>
    /// Корень хранилища: переменная окружения, потом выбранное человеком.
    /// Возвращает null, если место ещё не выбрано: тогда программа показывает
    /// экран выбора, а не догадывается. Раньше здесь стояло S:\, и это была
    /// настройка под одну машину, зашитая в код.
    /// </summary>
    public static Config? Load()
    {
        var root = Environment.GetEnvironmentVariable("TANBA_ROOT");
        if (string.IsNullOrWhiteSpace(root)) root = RootStore.Read();
        return string.IsNullOrWhiteSpace(root) ? null : new Config(root);
    }

    /// <summary>
    /// Переносит приём под новое имя. Звать до EnsureLayout: иначе рядом со
    /// старой папкой появится новая пустая, и человек увидит две.
    ///
    /// Ничего не удаляет и ничего не затирает. Один занятый файл больше не
    /// отменяет перенос остальных: каждый идёт своей попыткой. То, что не
    /// поддалось, остаётся в старой папке до следующего запуска, а пути в
    /// базе расставляет InboxMove.Carry по тому, где файл лежит на самом
    /// деле, а не по тому, куда мы собирались его положить.
    /// </summary>
    public void RenameOldInbox()
    {
        var old = Path.Combine(Root, OldInboxName);
        if (!Directory.Exists(old) || string.Equals(OldInboxName, InboxName, StringComparison.Ordinal)) return;

        // Целиком папкой это одно движение и не задевает открытые файлы:
        // переименовывается запись каталога, а не его содержимое.
        if (!Directory.Exists(Inbox))
        {
            try
            {
                Directory.Move(old, Inbox);
                Console.WriteLine($"Приём переименован: {OldInboxName} -> {InboxName}");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Папку кто-то держит открытой, обычно проводник. Дальше
                // попробуем по одному файлу: это медленнее, но проходит там,
                // где не проходит переименование целиком.
                Console.Error.WriteLine($"Приём целиком переименовать не вышло: {ex.Message}");
                Directory.CreateDirectory(Inbox);
            }
        }

        int moved = 0, stuck = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(old))
        {
            var to = FreeName(Path.Combine(Inbox, Path.GetFileName(path)));
            if (to is null) { stuck++; continue; }

            try { Directory.Move(path, to); moved++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Файл открыт в Corel. Остальные это не касается.
                stuck++;
                Console.Error.WriteLine($"Занят, оставляю на месте: {Path.GetFileName(path)}");
            }
        }

        if (stuck == 0 && !Directory.EnumerateFileSystemEntries(old).Any())
        {
            try { Directory.Delete(old); } catch (IOException) { }
            Console.WriteLine($"Приём перенесён в {InboxName}: {moved}, старая папка убрана");
        }
        else
        {
            Console.WriteLine($"Приём перенесён частично: {moved}, осталось {stuck}. Попробую при следующем запуске");
        }
    }

    /// <summary>
    /// Свободное имя рядом с занятым: «имя (2).cdr», как делает проводник.
    /// Возвращает null для папки, чей тёзка уже есть: сливать две папки это
    /// уже не переезд, а решение за человека.
    ///
    /// Нужно ровно для одного случая, и он оказался настоящим: Corel пишет
    /// резервные копии под одним именем, и в обеих папках их оказалось по
    /// одной, с разным содержимым. Затирать нельзя, бросать тоже: брошенная
    /// осталась бы на диске навсегда, а вместе с ней и старая папка.
    /// </summary>
    private static string? FreeName(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        if (Directory.Exists(path)) return null;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var n = 2; n <= 99; n++)
        {
            var next = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!File.Exists(next) && !Directory.Exists(next)) return next;
        }
        return null;
    }

    /// <summary>Создаёт структуру папок. Идемпотентно, можно звать каждый запуск.</summary>
    public void EnsureLayout()
    {
        if (!Directory.Exists(Root))
            throw new DirectoryNotFoundException($"Корень хранилища недоступен: {Root}");

        Directory.CreateDirectory(Inbox);
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(Junk);
        Directory.CreateDirectory(VersionsInbox);
        foreach (var d in ServiceDirs) Directory.CreateDirectory(d);

        // Служебное прячем, чтобы не мозолило глаза в проводнике.
        foreach (var d in ServiceDirs)
        {
            try { new DirectoryInfo(d).Attributes |= FileAttributes.Hidden; }
            catch (UnauthorizedAccessException) { /* не критично */ }
        }
    }

    /// <summary>Путь внутри хранилища относительно корня: то, что лежит в files.rel_path.</summary>
    public string ToRelative(string fullPath) =>
        Path.GetRelativePath(Root, fullPath);

    public string ToFull(string relPath) =>
        Path.GetFullPath(Path.Combine(Root, relPath));

    /// <summary>Лежит ли путь внутри служебной папки.</summary>
    public bool IsService(string fullPath)
    {
        foreach (var d in ServiceDirs)
            if (fullPath.StartsWith(d, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
