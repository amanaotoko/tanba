using Microsoft.Data.Sqlite;
using Tanba.Storage;

namespace Tanba.Web;

/// <summary>
/// Каталоги. Каталог это не папка на диске, а связь, которую видно:
/// файл может лежать в скольких угодно каталогах, ничего никуда не переносится.
/// Сам каталог живёт строкой в files с kind='catalog', поэтому теги, поиск,
/// отбор и карточка достаются ему бесплатно.
/// </summary>
public static class Catalogs
{
    public static void MapCatalogs(this WebApplication app, Repo repo)
    {
        // ── Создать ──────────────────────────────────────────────────────

        app.MapPost("/api/catalogs", (NewCatalog cmd) =>
        {
            var name = (cmd.Name ?? "").Trim();
            if (name.Length == 0) return Results.BadRequest(new { error = "пустое имя" });

            using var c = repo.Open();
            using var tx = c.BeginTransaction();

            var id = Create(c, name);

            if (cmd.ParentId is > 0)
            {
                var err = Put(c, cmd.ParentId.Value, [id]);
                if (err is not null) { tx.Rollback(); return Results.BadRequest(new { error = err }); }
            }

            if (cmd.ItemIds is { Length: > 0 })
            {
                var err = Put(c, id, cmd.ItemIds);
                if (err is not null) { tx.Rollback(); return Results.BadRequest(new { error = err }); }
            }

            Refresh(c, id);
            tx.Commit();
            return Results.Ok(new { id, name });
        });

        // ── Положить и вынуть ────────────────────────────────────────────

        app.MapPost("/api/catalogs/{id:long}/items", (long id, ItemsCmd cmd) =>
        {
            using var c = repo.Open();
            if (!IsCatalog(c, id)) return Results.NotFound(new { error = "каталога нет" });

            using var tx = c.BeginTransaction();
            var err = Put(c, id, cmd.ItemIds ?? []);
            if (err is not null) { tx.Rollback(); return Results.BadRequest(new { error = err }); }
            Refresh(c, id);
            tx.Commit();
            return Results.Ok(new { ok = true, count = Count(c, id) });
        });

        // Файл уходит только из этого каталога. С диска не девается,
        // в других каталогах остаётся.
        app.MapDelete("/api/catalogs/{id:long}/items", (long id, string? ids) =>
        {
            var itemIds = ParseIds(ids);
            if (itemIds.Length == 0) return Results.BadRequest(new { error = "нечего вынимать" });

            using var c = repo.Open();
            using var tx = c.BeginTransaction();
            using (var cmd = c.Sql(
                $"DELETE FROM catalog_items WHERE catalog_id = $c AND item_id IN ({string.Join(',', itemIds)})",
                ("$c", id)))
                cmd.ExecuteNonQuery();
            Refresh(c, id);
            tx.Commit();
            return Results.Ok(new { ok = true, count = Count(c, id) });
        });

        // ── Переименовать ────────────────────────────────────────────────

        app.MapPatch("/api/catalogs/{id:long}", (long id, NewCatalog cmd) =>
        {
            var name = (cmd.Name ?? "").Trim();
            if (name.Length == 0) return Results.BadRequest(new { error = "пустое имя" });

            using var c = repo.Open();
            if (!IsCatalog(c, id)) return Results.NotFound(new { error = "каталога нет" });

            using var upd = c.Sql("UPDATE files SET orig_name = $n WHERE id = $i", ("$n", name), ("$i", id));
            upd.ExecuteNonQuery();
            Repo.Index(c, id, name);
            return Results.Ok(new { id, name });
        });

        // ── Удалить ──────────────────────────────────────────────────────
        // Пропадает только связь. Файлы остаются на диске и в библиотеке,
        // вложенные каталоги всплывают на уровень выше.

        app.MapDelete("/api/catalogs/{id:long}", (long id) =>
        {
            using var c = repo.Open();
            if (!IsCatalog(c, id)) return Results.NotFound(new { error = "каталога нет" });

            using var tx = c.BeginTransaction();
            var parent = ParentOf(c, id);

            using (var lift = c.Sql("""
                UPDATE catalog_items SET catalog_id = $p
                WHERE catalog_id = $i
                  AND item_id IN (SELECT id FROM files WHERE kind = 'catalog')
                  AND $p IS NOT NULL
                """, ("$i", id), ("$p", parent)))
                lift.ExecuteNonQuery();

            using (var del = c.Sql("DELETE FROM files WHERE id = $i AND kind = 'catalog'", ("$i", id)))
                del.ExecuteNonQuery();

            if (parent is not null) Refresh(c, parent.Value);
            tx.Commit();
            return Results.Ok(new { ok = true });
        });

        // ── Что внутри и где я нахожусь ──────────────────────────────────

        app.MapGet("/api/catalogs/{id:long}", (long id) =>
        {
            using var c = repo.Open();
            var info = Info(c, id);
            if (info is null) return Results.NotFound(new { error = "каталога нет" });

            return Results.Ok(new
            {
                id,
                name = info.Value.Name,
                size = info.Value.Size,
                count = Count(c, id),
                // Путь наверх для крошек. У каталогов иерархия настоящая,
                // в отличие от тегов, поэтому и путь честный.
                path = PathUp(c, id),
                // Дерево от самого верхнего каталога: слева нужно видеть работу целиком.
                tree = Tree(c, TopOf(c, id)),
            });
        });
    }

    // ── Работа с составом ────────────────────────────────────────────────

    private static long Create(SqliteConnection c, string name)
    {
        // rel_path у каталога синтетический: байтов на диске нет, но колонка
        // обязательная и уникальная, поэтому кладём заведомо свободное значение.
        using var ins = c.Sql("""
            INSERT INTO files (kind, rel_path, orig_name, size, added_at, seen_at)
            VALUES ('catalog', 'catalog\' || lower(hex(randomblob(8))), $n, 0, $t, $t);
            SELECT last_insert_rowid();
            """, ("$n", name), ("$t", Repo.Now));

        // Каталог ищется наравне с файлами, значит и в указателе он наравне.
        var id = ins.ScalarLong();
        Repo.Index(c, id, name);
        return id;
    }

    /// <summary>Кладёт объекты в каталог. Возвращает текст ошибки либо null.</summary>
    private static string? Put(SqliteConnection c, long catalogId, long[] itemIds)
    {
        foreach (var item in itemIds.Where(x => x > 0).Distinct())
        {
            if (item == catalogId) return "каталог не может лежать сам в себе";

            // Циклы длиннее одного шага триггером не отловить: рекурсию
            // в условии триггера SQLite не пускает, поэтому проверяем здесь.
            if (IsCatalog(c, item) && IsInside(c, catalogId, item))
                return "так каталог окажется внутри самого себя";

            try
            {
                using var ins = c.Sql("""
                    INSERT OR IGNORE INTO catalog_items (catalog_id, item_id, sort_order, added_at)
                    VALUES ($c, $i, (SELECT ifnull(max(sort_order), 0) + 1 FROM catalog_items WHERE catalog_id = $c), $t)
                    """, ("$c", catalogId), ("$i", item), ("$t", Repo.Now));
                ins.ExecuteNonQuery();
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19)
            {
                return e.Message;   // сработал триггер единственного родителя
            }
        }
        return null;
    }

    /// <summary>Лежит ли maybeChild где-то внутри ancestor, на любой глубине.</summary>
    private static bool IsInside(SqliteConnection c, long maybeChild, long ancestor)
    {
        using var cmd = c.Sql("""
            WITH RECURSIVE up(id) AS (
              SELECT $start
              UNION
              SELECT ci.catalog_id FROM catalog_items ci JOIN up ON up.id = ci.item_id
            )
            SELECT count(*) FROM up WHERE id = $anc
            """, ("$start", maybeChild), ("$anc", ancestor));
        return cmd.ScalarLong() > 0;
    }

    // ── Пересчёт веса и обложки ──────────────────────────────────────────

    /// <summary>
    /// Вес каталога это сумма содержимого со всеми потрохами, обложка это
    /// лучший растровый файл внутри: у cdr эскиз бывает пустым.
    /// Пересчитываем и всех предков, они тоже потяжелели.
    /// </summary>
    private static void Refresh(SqliteConnection c, long catalogId)
    {
        long? id = catalogId;
        while (id is not null)
        {
            using (var cmd = c.Sql("""
                UPDATE files SET
                  size = (
                    WITH RECURSIVE down(id) AS (
                      SELECT item_id FROM catalog_items WHERE catalog_id = $i
                      UNION
                      SELECT ci.item_id FROM catalog_items ci JOIN down ON down.id = ci.catalog_id
                    )
                    SELECT ifnull(sum(f.size), 0) FROM files f
                    JOIN down ON down.id = f.id WHERE f.kind = 'file'
                  ),
                  cover_file_id = (
                    SELECT f.id FROM catalog_items ci JOIN files f ON f.id = ci.item_id
                    WHERE ci.catalog_id = $i AND f.kind = 'file'
                    ORDER BY CASE WHEN f.ext IN ('jpg','jpeg','png','webp','tif','tiff','bmp','gif') THEN 0 ELSE 1 END,
                             ci.sort_order
                    LIMIT 1
                  )
                WHERE id = $i AND kind = 'catalog'
                """, ("$i", id.Value)))
                cmd.ExecuteNonQuery();

            id = ParentOf(c, id.Value);
        }
    }

    // ── Мелкие запросы ───────────────────────────────────────────────────

    private static bool IsCatalog(SqliteConnection c, long id)
    {
        using var cmd = c.Sql("SELECT count(*) FROM files WHERE id = $i AND kind = 'catalog'", ("$i", id));
        return cmd.ScalarLong() > 0;
    }

    private static long? ParentOf(SqliteConnection c, long id)
    {
        using var cmd = c.Sql("SELECT catalog_id FROM catalog_items WHERE item_id = $i LIMIT 1", ("$i", id));
        var v = cmd.ScalarLong();
        return v > 0 ? v : null;
    }

    private static long TopOf(SqliteConnection c, long id)
    {
        var cur = id;
        for (var guard = 0; guard < 64; guard++)
        {
            var p = ParentOf(c, cur);
            if (p is null) return cur;
            cur = p.Value;
        }
        return cur;
    }

    private static long Count(SqliteConnection c, long id)
    {
        using var cmd = c.Sql("SELECT count(*) FROM catalog_items WHERE catalog_id = $i", ("$i", id));
        return cmd.ScalarLong();
    }

    private static (string Name, long Size)? Info(SqliteConnection c, long id)
    {
        using var cmd = c.Sql(
            "SELECT orig_name, ifnull(size, 0) FROM files WHERE id = $i AND kind = 'catalog'", ("$i", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetInt64(1)) : null;
    }

    /// <summary>Цепочка от самого верхнего каталога до текущего, для крошек.</summary>
    private static object[] PathUp(SqliteConnection c, long id)
    {
        var chain = new List<object>();
        long? cur = id;
        for (var guard = 0; cur is not null && guard < 64; guard++)
        {
            using (var cmd = c.Sql("SELECT orig_name FROM files WHERE id = $i", ("$i", cur.Value)))
            using (var r = cmd.ExecuteReader())
                if (r.Read()) chain.Insert(0, new { id = cur.Value, name = r.GetString(0) });
            cur = ParentOf(c, cur.Value);
        }
        return [.. chain];
    }

    /// <summary>Дерево вложенных каталогов от корня работы.</summary>
    private static object Tree(SqliteConnection c, long rootId)
    {
        using var cmd = c.Sql("""
            SELECT f.id, f.orig_name FROM catalog_items ci
            JOIN files f ON f.id = ci.item_id
            WHERE ci.catalog_id = $i AND f.kind = 'catalog'
            ORDER BY ci.sort_order, f.orig_name
            """, ("$i", rootId));

        var kids = new List<long>();
        var names = new Dictionary<long, string>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) { kids.Add(r.GetInt64(0)); names[r.GetInt64(0)] = r.GetString(1); }

        string rootName;
        using (var n = c.Sql("SELECT orig_name FROM files WHERE id = $i", ("$i", rootId)))
        using (var r = n.ExecuteReader())
            rootName = r.Read() ? r.GetString(0) : "";

        return new { id = rootId, name = rootName, children = kids.Select(k => Tree(c, k)).ToArray() };
    }

    private static long[] ParseIds(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : [.. s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Where(x => long.TryParse(x, out _)).Select(long.Parse)];
}

public sealed record NewCatalog(string? Name, long? ParentId, long[]? ItemIds);
public sealed record ItemsCmd(long[]? ItemIds);
