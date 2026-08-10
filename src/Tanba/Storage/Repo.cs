using Microsoft.Data.Sqlite;

namespace Tanba.Storage;

/// <summary>Доступ к данным. Ничего не знает про диск, только про базу.</summary>
public sealed class Repo(Db db)
{
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public SqliteConnection Open() => db.Open();

    // ── ФАЙЛЫ ────────────────────────────────────────────────────────────

    /// <summary>Заводит или обновляет запись о файле, лежащем в приёме.</summary>
    public long UpsertInbox(SqliteConnection c, string relPath, string origName,
        string? ext, long size, string sha256, long mtime, long ctime)
    {
        using var find = c.Sql("SELECT id FROM files WHERE rel_path=$p", ("$p", relPath));
        var id = find.ScalarLong();

        if (id > 0)
        {
            using var upd = c.Sql("""
                UPDATE files SET size=$s, sha256=$h, file_mtime=$m, file_ctime=$ct,
                                 seen_at=$n, is_missing=0
                WHERE id=$i
                """,
                ("$s", size), ("$h", sha256), ("$m", mtime), ("$ct", ctime), ("$n", Now), ("$i", id));
            upd.ExecuteNonQuery();
            return id;
        }

        using var ins = c.Sql("""
            INSERT INTO files (kind, rel_path, orig_name, ext, size, sha256,
                               file_ctime, file_mtime, added_at, seen_at)
            VALUES ('file', $p, $o, $e, $s, $h, $ct, $m, $n, $n);
            SELECT last_insert_rowid();
            """,
            ("$p", relPath), ("$o", origName), ("$e", ext), ("$s", size),
            ("$h", sha256), ("$ct", ctime), ("$m", mtime), ("$n", Now));

        var made = ins.ScalarLong();
        Index(c, made, origName);
        return made;
    }

    /// <summary>
    /// Кладёт имя в указатель поиска. Звать после любого создания записи
    /// и после любого переименования: указатель, разошедшийся с данными,
    /// хуже отсутствующего, потому что врёт молча.
    ///
    /// Сначала удаляем, потом вставляем: в FTS5 нет обновления на месте,
    /// а вставка поверх завела бы вторую строку с тем же rowid.
    /// </summary>
    public static void Index(SqliteConnection c, long id, string? name)
    {
        using var del = c.Sql("DELETE FROM files_fts WHERE rowid = $i", ("$i", id));
        del.ExecuteNonQuery();

        if (string.IsNullOrEmpty(name)) return;
        using var ins = c.Sql("INSERT INTO files_fts (rowid, name) VALUES ($i, $n)",
            ("$i", id), ("$n", name));
        ins.ExecuteNonQuery();
    }

    /// <summary>Файлы, лежащие в папке приёма и ещё не разложенные.</summary>
    public List<FileRow> ListInbox(SqliteConnection c)
    {
        using var cmd = c.Sql("""
            SELECT id, rel_path, orig_name, ext, size, sha256, added_at
            FROM files
            WHERE is_missing = 0 AND rel_path LIKE $pfx
            ORDER BY added_at, id
            """, ("$pfx", Config.InboxName + @"\%"));
        return ReadFiles(cmd);
    }

    public FileRow? Get(SqliteConnection c, long id)
    {
        using var cmd = c.Sql("""
            SELECT id, rel_path, orig_name, ext, size, sha256, added_at FROM files WHERE id=$i
            """, ("$i", id));
        return ReadFiles(cmd).FirstOrDefault();
    }

    /// <summary>Уже разложенный файл с таким же содержимым, если он есть.</summary>
    public long FindFiledByHash(SqliteConnection c, string sha256, long exceptId)
    {
        using var cmd = c.Sql("""
            SELECT id FROM files
            WHERE sha256 = $h AND id <> $x AND is_missing = 0 AND rel_path NOT LIKE $pfx
            LIMIT 1
            """, ("$h", sha256), ("$x", exceptId), ("$pfx", Config.InboxName + @"\%"));
        return cmd.ScalarLong();
    }

    public void SetRelPath(SqliteConnection c, long id, string relPath)
    {
        using var cmd = c.Sql("UPDATE files SET rel_path=$p, seen_at=$n WHERE id=$i",
            ("$p", relPath), ("$n", Now), ("$i", id));
        cmd.ExecuteNonQuery();
    }

    public void SetOrigName(SqliteConnection c, long id, string name)
    {
        using var cmd = c.Sql("UPDATE files SET orig_name=$n WHERE id=$i", ("$n", name), ("$i", id));
        cmd.ExecuteNonQuery();
        Index(c, id, name);
    }

    /// <summary>
    /// Запоминает номер файла на томе. По нему файл узнаётся после того,
    /// как его переложили мимо программы, см. Shell.Native.FileId.
    /// </summary>
    public void SetFileId(SqliteConnection c, long id, string? fileId)
    {
        if (fileId is null) return;
        using var cmd = c.Sql("UPDATE files SET file_id = $f WHERE id = $i", ("$f", fileId), ("$i", id));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Помечает файл пропавшим: на диске его нет, а строка с тегами нужна.</summary>
    public void MarkMissing(SqliteConnection c, long id)
    {
        using var cmd = c.Sql("UPDATE files SET is_missing = 1 WHERE id = $i", ("$i", id));
        cmd.ExecuteNonQuery();
    }

    public void Delete(SqliteConnection c, long id)
    {
        Index(c, id, null);
        using var cmd = c.Sql("DELETE FROM files WHERE id=$i", ("$i", id));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Файлы, пропавшие из приёма между сканированиями, убираем из базы.</summary>
    /// <summary>
    /// Убирает из приёма строки файлов, которых больше нет. Возвращает
    /// удалённые id, чтобы звавший выбросил их эскизы: id в таблице
    /// переиспользуются, и чужой эскиз потом показался бы новому файлу.
    ///
    /// Проверка onDisk обязательна и не для красоты: отсутствие в списке
    /// увиденных значит лишь то, что сканер до файла не добрался.
    /// </summary>
    public List<long> ForgetMissingInbox(SqliteConnection c, IEnumerable<string> seenRelPaths,
        Func<string, bool> onDisk)
    {
        var seen = new HashSet<string>(seenRelPaths, StringComparer.OrdinalIgnoreCase);
        var doomed = ListInbox(c)
            .Where(f => !seen.Contains(f.RelPath) && !onDisk(f.RelPath))
            .Select(f => f.Id).ToList();
        foreach (var id in doomed) Delete(c, id);
        return doomed;
    }

    private static List<FileRow> ReadFiles(SqliteCommand cmd)
    {
        var list = new List<FileRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var rel = r.GetString(1);
            list.Add(new FileRow(
                r.GetInt64(0), rel, r.GetString(2), r.Str(3), r.Num(4), r.Str(5), r.GetInt64(6),
                Pending: rel.StartsWith(Config.InboxName + @"\", StringComparison.OrdinalIgnoreCase)));
        }
        return list;
    }

    // ── ГРУППЫ И ТЕГИ ────────────────────────────────────────────────────

    public List<GroupRow> Groups(SqliteConnection c)
    {
        var tags = new Dictionary<long, List<TagRow>>();
        using (var cmd = c.Sql("""
            SELECT t.id, t.group_id, t.parent_id, t.name, t.sort_order,
                   (SELECT count(*) FROM file_tags ft WHERE ft.tag_id = t.id)
            FROM tags t ORDER BY t.sort_order, t.name
            """))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var t = new TagRow(r.GetInt64(0), r.GetInt64(1), r.Num(2), r.GetString(3),
                                   r.GetInt32(4), r.GetInt64(5));
                if (!tags.TryGetValue(t.GroupId, out var l)) tags[t.GroupId] = l = [];
                l.Add(t);
            }

        var groups = new List<GroupRow>();
        using (var cmd = c.Sql("SELECT id, name, is_multi, is_required, sort_order FROM tag_groups ORDER BY sort_order, name"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var id = r.GetInt64(0);
                groups.Add(new GroupRow(id, r.GetString(1), r.GetInt64(2) != 0, r.GetInt64(3) != 0,
                    r.GetInt32(4), tags.GetValueOrDefault(id, [])));
            }
        return groups;
    }

    public long EnsureTag(SqliteConnection c, long groupId, string name)
    {
        using var find = c.Sql(
            "SELECT id FROM tags WHERE group_id=$g AND ifnull(parent_id,0)=0 AND name=$n",
            ("$g", groupId), ("$n", name));
        var id = find.ScalarLong();
        if (id > 0) return id;

        using var ins = c.Sql("""
            INSERT INTO tags (group_id, name) VALUES ($g, $n);
            SELECT last_insert_rowid();
            """, ("$g", groupId), ("$n", name));
        return ins.ScalarLong();
    }

    public long EnsureGroup(SqliteConnection c, string name, bool isMulti = true)
    {
        using var find = c.Sql("SELECT id FROM tag_groups WHERE name=$n", ("$n", name));
        var id = find.ScalarLong();
        if (id > 0) return id;

        using var ins = c.Sql("""
            INSERT INTO tag_groups (name, is_multi, sort_order)
            VALUES ($n, $m, (SELECT ifnull(max(sort_order),0)+1 FROM tag_groups));
            SELECT last_insert_rowid();
            """, ("$n", name), ("$m", isMulti ? 1 : 0));
        return ins.ScalarLong();
    }

    // ── СВЯЗЬ ФАЙЛ ↔ ТЕГ ─────────────────────────────────────────────────

    /// <summary>Сколько из выделенных файлов имеют каждый тег, это нужно tri-state галочкам.</summary>
    public Dictionary<long, TagState> TagStates(SqliteConnection c, IReadOnlyList<long> fileIds)
    {
        var result = new Dictionary<long, TagState>();
        if (fileIds.Count == 0) return result;

        var ids = string.Join(',', fileIds);
        using var cmd = c.Sql($"SELECT tag_id, count(*) FROM file_tags WHERE file_id IN ({ids}) GROUP BY tag_id");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var n = r.GetInt64(1);
            result[r.GetInt64(0)] = n >= fileIds.Count ? TagState.On : TagState.Partial;
        }
        return result;
    }

    public IReadOnlyList<long> TagsOf(SqliteConnection c, long fileId)
    {
        var list = new List<long>();
        using var cmd = c.Sql("SELECT tag_id FROM file_tags WHERE file_id=$f", ("$f", fileId));
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    /// <summary>Вешает или снимает тег на пачке файлов. Уважает группы с единственным выбором.</summary>
    public void ApplyTag(SqliteConnection c, IReadOnlyList<long> fileIds, long tagId, bool on)
    {
        if (fileIds.Count == 0) return;

        if (!on)
        {
            using var del = c.Sql(
                $"DELETE FROM file_tags WHERE tag_id=$t AND file_id IN ({string.Join(',', fileIds)})",
                ("$t", tagId));
            del.ExecuteNonQuery();
            LogEvent(c, "tag_remove", null, tagId);
            return;
        }

        // В группе с is_multi=0 новый тег вытесняет прежний.
        using (var single = c.Sql("""
            SELECT g.is_multi, g.id FROM tags t JOIN tag_groups g ON g.id = t.group_id WHERE t.id=$t
            """, ("$t", tagId)))
        using (var r = single.ExecuteReader())
            if (r.Read() && r.GetInt64(0) == 0)
            {
                var groupId = r.GetInt64(1);
                using var wipe = c.Sql($"""
                    DELETE FROM file_tags
                    WHERE file_id IN ({string.Join(',', fileIds)})
                      AND tag_id IN (SELECT id FROM tags WHERE group_id=$g)
                    """, ("$g", groupId));
                wipe.ExecuteNonQuery();
            }

        foreach (var fid in fileIds)
        {
            using var ins = c.Sql("""
                INSERT OR IGNORE INTO file_tags (file_id, tag_id, source, added_at)
                VALUES ($f, $t, 'manual', $n)
                """, ("$f", fid), ("$t", tagId), ("$n", Now));
            ins.ExecuteNonQuery();
        }
        LogEvent(c, "tag_add", null, tagId);
    }

    /// <summary>Переносит теги с одного файла на другой при схлопывании дубликатов.</summary>
    public void MergeTags(SqliteConnection c, long fromFile, long toFile)
    {
        using var cmd = c.Sql("""
            INSERT OR IGNORE INTO file_tags (file_id, tag_id, source, added_at)
            SELECT $to, tag_id, 'import', $n FROM file_tags WHERE file_id = $from
            """, ("$to", toFile), ("$from", fromFile), ("$n", Now));
        cmd.ExecuteNonQuery();
    }

    // ── ПРОЧЕЕ ───────────────────────────────────────────────────────────

    public List<IgnoreRule> IgnoreRules(SqliteConnection c)
    {
        var list = new List<IgnoreRule>();
        using var cmd = c.Sql("SELECT pattern, action FROM ignore_rules");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new IgnoreRule(r.GetString(0), r.GetString(1)));
        return list;
    }

    public void LogEvent(SqliteConnection c, string kind, long? fileId, long? tagId, string? payload = null)
    {
        using var cmd = c.Sql("""
            INSERT INTO events (at, kind, file_id, tag_id, payload) VALUES ($n, $k, $f, $t, $p)
            """, ("$n", Now), ("$k", kind), ("$f", fileId), ("$t", tagId), ("$p", payload));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Три числа для вкладок. Отдельно от Stats и от /api/state потому, что
    /// вкладки висят на каждом экране, а state тащит с собой весь приём вместе
    /// с тегами каждого файла: сотня строк ради трёх чисел.
    ///
    /// Приём считается тем же условием, что и в ListInbox, иначе вкладка и
    /// сам экран разошлись бы в показаниях.
    /// </summary>
    public (long Inbox, long Files, long Tags) Counts(SqliteConnection c)
    {
        using var cmd = c.Sql("""
            SELECT (SELECT count(*) FROM files WHERE is_missing=0 AND rel_path LIKE $pfx),
                   (SELECT count(*) FROM files WHERE is_missing=0 AND rel_path NOT LIKE $pfx),
                   (SELECT count(*) FROM tags)
            """, ("$pfx", Config.InboxName + @"\%"));
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
    }

    /// <summary>Счётчики для шапки. Файлы из приёма ещё не в каталоге, их не считаем.</summary>
    public (long Files, long Untagged, long Bytes) Stats(SqliteConnection c)
    {
        using var cmd = c.Sql("""
            SELECT (SELECT count(*) FROM files WHERE is_missing=0 AND rel_path NOT LIKE $pfx),
                   (SELECT count(*) FROM v_untagged WHERE rel_path NOT LIKE $pfx),
                   (SELECT ifnull(sum(size),0) FROM files WHERE is_missing=0 AND rel_path NOT LIKE $pfx)
            """, ("$pfx", Config.InboxName + @"\%"));
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
    }
}
