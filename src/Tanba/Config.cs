namespace Tanba;

/// <summary>
/// Раскладка хранилища. Всё считается от корня диска.
/// Переопределяется переменной окружения TANBA_ROOT.
/// </summary>
public sealed class Config
{
    public const string InboxName = "СОХРАНИ СЮДА";

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

    public static Config Load()
    {
        var root = Environment.GetEnvironmentVariable("TANBA_ROOT");
        if (string.IsNullOrWhiteSpace(root)) root = @"S:\";
        return new Config(root);
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
