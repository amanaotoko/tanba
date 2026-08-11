using Tanba.Scanner;
using Tanba.Storage;

namespace Tanba.Tests;

/// <summary>
/// Хранилище во временной папке: настоящие файлы, настоящая база, настоящая
/// NTFS. Подделок нет намеренно. Всё, что здесь может сломаться, ломается
/// именно на файловой системе: номера файлов, блокировки, перенос между
/// папками. Подделка проверяла бы подделку.
/// </summary>
public sealed class Bench : IDisposable
{
    public Config Cfg { get; }
    public Db Db { get; }
    public Repo Repo { get; }
    public Ingest Ingest { get; }
    public StoreScan Store { get; }

    public Bench()
    {
        var dir = Directory.CreateTempSubdirectory("tanba-");
        Cfg = new Config(dir.FullName);
        Cfg.EnsureLayout();

        var db = new Db(Cfg.DbPath);
        var fresh = db.EnsureSchema();
        db.Migrate();
        Db = db;

        Repo = new Repo(db);
        // Стартовые теги больше не приходят из schema.sql, их заводит Seed.
        // Тестам нужен хотя бы один, см. SomeTag.
        if (fresh) Seed.Starter(Repo, Tanba.Lang.Ru);
        Ingest = new Ingest(Cfg, Repo);
        Store = new StoreScan(Cfg, Repo);
    }

    /// <summary>Кладёт файл в приём, как это делает человек.</summary>
    public string Drop(string name, string text = "содержимое")
    {
        var p = Path.Combine(Cfg.Inbox, name);
        File.WriteAllText(p, text);
        return p;
    }

    /// <summary>Первый попавшийся тег: какой именно, тестам безразлично.</summary>
    public long SomeTag()
    {
        using var c = Repo.Open();
        return Repo.Groups(c)[0].Tags[0].Id;
    }

    /// <summary>Сканирует приём, помечает всё найденное тегом и раскладывает.</summary>
    public long FileEverything()
    {
        Ingest.ScanInbox();
        long id;
        using (var c = Repo.Open())
        {
            id = Repo.ListInbox(c)[0].Id;
            Repo.ApplyTag(c, [id], SomeTag(), true);
        }
        Ingest.File([id]);
        return id;
    }

    public string[] Filed() =>
        Directory.Exists(Cfg.Store)
            ? [.. Directory.EnumerateFiles(Cfg.Store, "*", SearchOption.AllDirectories)]
            : [];

    /// <summary>Одно значение из базы: тестам хватает, отдельного слоя не заводим.</summary>
    public string? Cell(string column, long id)
    {
        using var c = Repo.Open();
        using var cmd = c.Sql($"SELECT {column} FROM files WHERE id = $i", ("$i", id));
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToString(v);
    }

    public void Dispose()
    {
        try { Directory.Delete(Cfg.Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
