using Microsoft.Data.Sqlite;
using Tanba.Shell;
using Tanba.Storage;

namespace Tanba.Scanner;

/// <summary>
/// Следит за хранилищем, пока программа работает. События сыплются пачками
/// (одно перетаскивание в проводнике это десятки сообщений), поэтому обход
/// запускается один раз после затишья.
///
/// Того, что случилось без нас, наблюдатель не увидит: это добирает обход
/// при запуске, узнавая файлы по номеру.
/// </summary>
public sealed class StoreWatcher : IDisposable
{
    private readonly FileSystemWatcher _fsw;
    private readonly System.Threading.Timer _debounce;
    private readonly StoreScan _scan;
    private int _running;

    public event Action<StoreReport>? Done;

    public StoreWatcher(Config cfg, StoreScan scan)
    {
        _scan = scan;
        Directory.CreateDirectory(cfg.Store);

        _debounce = new System.Threading.Timer(_ => Run(), null, Timeout.Infinite, Timeout.Infinite);

        _fsw = new FileSystemWatcher(cfg.Store)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
        };
        _fsw.Created += (_, _) => Bump();
        _fsw.Deleted += (_, _) => Bump();
        _fsw.Renamed += (_, _) => Bump();
        _fsw.Error += (_, _) => Bump();
        _fsw.EnableRaisingEvents = true;
    }

    /// Две секунды тишины: копирование пачки файлов длится дольше события.
    private void Bump() => _debounce.Change(2000, Timeout.Infinite);

    public void Run()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) { Bump(); return; }
        try
        {
            var r = _scan.Run();
            if (r.Anything) Done?.Invoke(r);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Обход хранилища упал: {ex.Message}");
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    /// <summary>Первый обход в фоне, чтобы не держать запуск.</summary>
    public void Start() => Task.Run(Run);

    public void Dispose()
    {
        _fsw.EnableRaisingEvents = false;
        _fsw.Dispose();
        _debounce.Dispose();
    }
}

/// <summary>
/// Что нашёл обход хранилища. Tracked и Total не про находки, а про
/// готовность: сколько файлов помнят свой номер на томе. Пока номер не
/// проставлен, файл после переезда не найти, и знать это надо заранее,
/// а не в тот день, когда что-то потерялось.
/// </summary>
public sealed record StoreReport(
    int Followed, int MovedOut, int Lost, int Back, int Adopted, int Refused, int Tracked, int Total)
{
    public bool Anything => Followed + MovedOut + Lost + Back + Adopted + Refused > 0;

    public override string ToString() =>
        $"переехало внутри {Followed}, уехало наружу {MovedOut}, " +
        $"пропало {Lost}, вернулось {Back}, подобрано чужих {Adopted}" +
        (Refused > 0 ? $", НЕ ТРОНУТО из осторожности {Refused}" : "");
}

/// <summary>
/// Сторож хранилища. Раньше за файлами после «Разложить» не следил никто:
/// файл можно было переименовать или переложить в проводнике, и программа
/// не замечала даже того, что он пропал. Ломалось молча, в момент открытия.
///
/// Порядок проверок важен и не переставляется. Переложенный файл на диске
/// выглядит ровно как чужой, и если сначала подбирать чужих, а потом искать
/// переехавших, программа утащит в приём файл, который просто переложили.
/// Поэтому сперва опознание по номеру, и лишь непознанное считается чужим.
/// </summary>
public sealed class StoreScan(Config cfg, Repo repo)
{
    public StoreReport Run()
    {
        using var c = repo.Open();

        var rows = Rows(c);
        var misses = new List<Row>();
        int back = 0;

        // Проход первый, дешёвый: файл на месте? Номера тут не спрашиваем,
        // на каждый пришлось бы открывать файл, а таких проверок тысячи.
        foreach (var r in rows)
        {
            if (!System.IO.File.Exists(cfg.ToFull(r.Rel))) { misses.Add(r); continue; }

            var recovered = r.Missing || r.MovedTo is not null;

            // Номер перечитываем, только если его нет или файл вернулся: у
            // вернувшегося это может быть уже другой файл, положенный на то же
            // место, и старый номер указывал бы в пустоту. Спрашивать номер
            // у всех на каждом обходе значило бы открывать тысячи файлов зря.
            if (r.FileId is null || recovered) SetFileId(c, r.Id, Native.FileId(cfg.ToFull(r.Rel)));
            if (recovered) { Found(c, r.Id, r.Rel); back++; }
        }

        var known = rows.Select(r => r.Rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var strangers = Strangers(known);

        // Ничего не пропало и чужого нет: расходимся, диск больше не трогаем.
        if (misses.Count == 0 && strangers.Count == 0)
            return Ready(c, 0, 0, 0, back, 0, 0);

        // Номера считаем только у тех, кто под подозрением: у непознанных
        // файлов в хранилище и, если этого мало, у всего остального на диске.
        var byId = Ids(strangers);
        if (misses.Any(m => m.FileId is not null && !byId.ContainsKey(m.FileId)))
            foreach (var (id, path) in Ids(Everything(known))) byId.TryAdd(id, path);

        int followed = 0, movedOut = 0, lost = 0;
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in misses)
        {
            if (r.FileId is null || !byId.TryGetValue(r.FileId, out var now))
            {
                if (!r.Missing) { Lose(c, r.Id); lost++; }
                continue;
            }

            claimed.Add(now);
            var rel = cfg.ToRelative(now);

            if (Inside(now, cfg.Store))
            {
                // Переезд внутри хранилища. Имя принимаем то, которое человек
                // дал файлу сам, но номерной префикс возвращаем: по нему
                // хранилище остаётся единообразным, а имя всё равно живёт в базе.
                var name = Strip(Path.GetFileName(now));
                var want = Path.Combine(Path.GetDirectoryName(now)!, $"{r.Id:D6}__{name}");
                if (!string.Equals(now, want, StringComparison.OrdinalIgnoreCase))
                    try { System.IO.File.Move(now, want); now = want; }
                    catch (IOException) { /* занят, поправим на следующем обходе */ }

                Follow(c, r.Id, cfg.ToRelative(now), name);
                followed++;
            }
            else
            {
                // Уехал за пределы хранилища, но остался на диске. Не тащим
                // обратно молча: файл вне архива не попадёт ни в копию базы,
                // ни в catalog.csv, и человек должен об этом узнать.
                MoveOut(c, r.Id, rel);
                movedOut++;
            }
        }

        var loose = strangers.Where(p => !claimed.Contains(p)).ToList();

        // Хранилище полное, а база пустая или почти пустая. Это не «все файлы
        // чужие», это потерянная или разрушенная база: файл базы удалили,
        // испортили, или хранилище перенесли на другую машину без него.
        // Разложить весь архив по приёму в этом случае значит срезать номера,
        // сплющить раскладку по годам и оставить копию базы, которая указывает
        // в пустоту. Собрать обратно будет нечем, поэтому не трогаем и говорим.
        if (loose.Count > 0 && loose.Count > rows.Count)
        {
            Console.Error.WriteLine(
                $"Хранилище: {loose.Count} файлов не числятся в базе, а в базе всего {rows.Count}. " +
                "Похоже на потерянную базу, ничего не трогаю.");
            return Ready(c, followed, movedOut, lost, back, 0, loose.Count);
        }

        return Ready(c, followed, movedOut, lost, back, Adopt(loose), 0);
    }

    /// <summary>Дописывает в отчёт, сколько файлов уже помнят свой номер.</summary>
    private static StoreReport Ready(SqliteConnection c, int f, int m, int l, int b, int a, int refused)
    {
        using var cmd = c.Sql("""
            SELECT count(*), count(file_id) FROM files
            WHERE kind = 'file' AND is_missing = 0 AND rel_path NOT LIKE $pfx
            """, ("$pfx", Config.InboxName + @"\%"));
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new StoreReport(f, m, l, b, a, refused, (int)r.GetInt64(1), (int)r.GetInt64(0))
            : new StoreReport(f, m, l, b, a, refused, 0, 0);
    }

    // ── Что лежит на диске ───────────────────────────────────────────────

    /// <summary>Файлы хранилища, которых нет ни в одной строке базы.</summary>
    private List<string> Strangers(HashSet<string> known)
    {
        if (!Directory.Exists(cfg.Store)) return [];
        return [.. Walk(cfg.Store).Where(p => !known.Contains(cfg.ToRelative(p)))];
    }

    /// <summary>
    /// Всё остальное на диске, куда файл мог уехать. Служебные папки минуем:
    /// эскизы и резервные копии базы к делу не относятся, а версии файлов
    /// живут своей таблицей и в files их нет.
    /// </summary>
    private List<string> Everything(HashSet<string> known)
    {
        var skip = new[] { cfg.Thumbs, cfg.Meta, cfg.Versions, cfg.Store };
        return [.. Walk(cfg.Root)
            .Where(p => !skip.Any(s => Inside(p, s)))
            .Where(p => !known.Contains(cfg.ToRelative(p)))];
    }

    /// <summary>
    /// Обход папки. Список собирается ЗДЕСЬ, внутри try, и это не стилистика:
    /// EnumerateFiles ленив, и раньше исключение случалось позже, при переборе,
    /// уже за пределами catch. На диске с «System Volume Information» обход
    /// падал целиком, и вся слежка за файлами молча выключалась.
    ///
    /// IgnoreInaccessible лечит причину: чужие защищённые папки пропускаются,
    /// а не роняют проход. Точки повторного разбора минуем, чтобы не ходить
    /// по кругу через ссылку на родительскую папку.
    /// </summary>
    private static List<string> Walk(string dir)
    {
        var how = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        try { return [.. Directory.EnumerateFiles(dir, "*", how)]; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static Dictionary<string, string> Ids(IEnumerable<string> paths)
    {
        var map = new Dictionary<string, string>();
        foreach (var p in paths)
        {
            var id = Native.FileId(p);
            if (id is not null) map.TryAdd(id, p);
        }
        return map;
    }

    private static bool Inside(string path, string dir) =>
        path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Убирает номерной префикс, если он остался в имени.</summary>
    private static string Strip(string name)
    {
        var i = name.IndexOf("__", StringComparison.Ordinal);
        return i is > 0 and <= 12 && name[..i].All(char.IsDigit) ? name[(i + 2)..] : name;
    }

    // ── Чужие файлы ──────────────────────────────────────────────────────

    /// <summary>
    /// Забирает в приём то, что положили в хранилище мимо программы.
    /// Именно в приём, а не в каталог молча: у такого файла нет ни тегов,
    /// ни даты попадания, и решать про него должен человек.
    /// </summary>
    private int Adopt(IEnumerable<string> paths)
    {
        var n = 0;
        Directory.CreateDirectory(cfg.Inbox);

        foreach (var p in paths)
        {
            try
            {
                var dst = cfg.Inbox + "\\" + Native.SafeName(Strip(Path.GetFileName(p)));
                for (var i = 2; System.IO.File.Exists(dst); i++)
                    dst = Path.Combine(cfg.Inbox,
                        $"{Path.GetFileNameWithoutExtension(p)} ({i}){Path.GetExtension(p)}");

                System.IO.File.Move(p, dst);
                n++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return n;
    }

    // ── База ─────────────────────────────────────────────────────────────

    private sealed record Row(long Id, string Rel, string? FileId, bool Missing, string? MovedTo);

    private static List<Row> Rows(SqliteConnection c)
    {
        var list = new List<Row>();
        using var cmd = c.Sql("""
            SELECT id, rel_path, file_id, is_missing, moved_to
            FROM files
            WHERE kind = 'file' AND rel_path NOT LIKE $pfx
            """, ("$pfx", Config.InboxName + @"\%"));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Row(r.GetInt64(0), r.GetString(1), r.Str(2), r.GetInt64(3) != 0, r.Str(4)));
        return list;
    }

    private static void SetFileId(SqliteConnection c, long id, string? fileId)
    {
        if (fileId is null) return;
        using var cmd = c.Sql("UPDATE files SET file_id = $f WHERE id = $i", ("$f", fileId), ("$i", id));
        cmd.ExecuteNonQuery();
    }

    private static void Found(SqliteConnection c, long id, string rel)
    {
        using var cmd = c.Sql(
            "UPDATE files SET is_missing = 0, moved_to = NULL, seen_at = $n WHERE id = $i",
            ("$n", Repo.Now), ("$i", id));
        cmd.ExecuteNonQuery();
    }

    private static void Follow(SqliteConnection c, long id, string rel, string name)
    {
        using var cmd = c.Sql("""
            UPDATE files SET rel_path = $p, orig_name = $n, is_missing = 0,
                             moved_to = NULL, seen_at = $t
            WHERE id = $i
            """, ("$p", rel), ("$n", name), ("$t", Repo.Now), ("$i", id));
        cmd.ExecuteNonQuery();
        Repo.Index(c, id, name);
    }

    private static void MoveOut(SqliteConnection c, long id, string where)
    {
        using var cmd = c.Sql(
            "UPDATE files SET moved_to = $w, is_missing = 0, seen_at = $t WHERE id = $i",
            ("$w", where), ("$t", Repo.Now), ("$i", id));
        cmd.ExecuteNonQuery();
    }

    private static void Lose(SqliteConnection c, long id)
    {
        using var cmd = c.Sql("UPDATE files SET is_missing = 1, moved_to = NULL WHERE id = $i",
            ("$i", id));
        cmd.ExecuteNonQuery();
    }
}
