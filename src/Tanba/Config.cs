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
    /// Ничего не удаляет. Если новая папка почему-то уже есть, содержимое
    /// перекладывается в неё по одному файлу, а старая уходит только пустой.
    /// Пути в базе правит Db.Migrate, и именно в таком порядке: сначала диск,
    /// потом база. Обратный порядок оставил бы базу указывающей в пустоту,
    /// а этот всего лишь заставит ближайший обход подождать одного запуска.
    /// </summary>
    public void RenameOldInbox()
    {
        var old = Path.Combine(Root, OldInboxName);
        if (!Directory.Exists(old) || string.Equals(OldInboxName, InboxName, StringComparison.Ordinal)) return;

        try
        {
            if (!Directory.Exists(Inbox))
            {
                Directory.Move(old, Inbox);
                Console.WriteLine($"Приём переименован: {OldInboxName} -> {InboxName}");
                return;
            }

            foreach (var path in Directory.EnumerateFileSystemEntries(old))
            {
                var to = Path.Combine(Inbox, Path.GetFileName(path));
                if (File.Exists(to) || Directory.Exists(to)) continue;  // тёзка на месте, не трогаем
                Directory.Move(path, to);
            }

            if (!Directory.EnumerateFileSystemEntries(old).Any())
            {
                Directory.Delete(old);
                Console.WriteLine($"Приём перенесён в {InboxName}, старая папка убрана");
            }
            else
            {
                Console.WriteLine($"В «{OldInboxName}» осталось непереносимое, папка оставлена как есть");
            }
        }
        catch (IOException ex)
        {
            // Файл держит Corel или проводник. Не беда: попробуем на следующем
            // запуске, а до тех пор обе папки на месте и ничего не потеряно.
            Console.Error.WriteLine($"Не смог переименовать приём: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Не смог переименовать приём: {ex.Message}");
        }
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
