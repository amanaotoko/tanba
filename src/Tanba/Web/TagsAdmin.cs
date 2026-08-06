using Tanba.Storage;

namespace Tanba.Web;

/// <summary>
/// Редактор структуры тегов: группы и теги, их переименование, перенос,
/// объединение и удаление. Всё, что меняет саму разметку, а не расставляет её.
/// </summary>
public static class TagsAdmin
{
    public static void MapTagsAdmin(this WebApplication app, Repo repo)
    {
        // ── Дерево целиком ───────────────────────────────────────────────

        app.MapGet("/api/tagtree", () =>
        {
            using var c = repo.Open();
            var groups = repo.Groups(c);

            return Results.Ok(new
            {
                groups = groups.Select(g => new
                {
                    id = g.Id,
                    name = g.Name,
                    isMulti = g.IsMulti,
                    isRequired = g.IsRequired,
                    sortOrder = g.SortOrder,
                    tags = g.Tags.Select(t => new
                    {
                        id = t.Id,
                        parentId = t.ParentId,
                        name = t.Name,
                        count = t.FileCount,
                    }),
                }),
            });
        });

        // ── Группы ───────────────────────────────────────────────────────

        app.MapPatch("/api/groups/{id:long}", (long id, GroupPatch p) =>
        {
            using var c = repo.Open();

            if (p.Name is { } nm)
            {
                var name = nm.Trim();
                if (name.Length == 0) return Results.BadRequest(new { error = "пустое имя" });
                using var dup = c.Sql("SELECT count(*) FROM tag_groups WHERE name = $n AND id <> $i",
                    ("$n", name), ("$i", id));
                if (dup.ScalarLong() > 0) return Results.BadRequest(new { error = "такая группа уже есть" });
                using var cmd = c.Sql("UPDATE tag_groups SET name = $n WHERE id = $i", ("$n", name), ("$i", id));
                cmd.ExecuteNonQuery();
            }

            if (p.IsMulti is { } m)
            {
                using var cmd = c.Sql("UPDATE tag_groups SET is_multi = $v WHERE id = $i",
                    ("$v", m ? 1 : 0), ("$i", id));
                cmd.ExecuteNonQuery();
            }

            if (p.IsRequired is { } r)
            {
                using var cmd = c.Sql("UPDATE tag_groups SET is_required = $v WHERE id = $i",
                    ("$v", r ? 1 : 0), ("$i", id));
                cmd.ExecuteNonQuery();
            }

            if (p.SortOrder is { } s)
            {
                using var cmd = c.Sql("UPDATE tag_groups SET sort_order = $v WHERE id = $i",
                    ("$v", s), ("$i", id));
                cmd.ExecuteNonQuery();
            }

            return Results.Ok(new { ok = true });
        });

        // Удаление группы уносит её теги и всю расстановку. Поэтому сначала
        // считаем, скольких файлов это коснётся, и без явного согласия не трогаем.
        app.MapDelete("/api/groups/{id:long}", (long id, bool? force) =>
        {
            using var c = repo.Open();

            long tags, marks;
            using (var cmd = c.Sql("""
                SELECT (SELECT count(*) FROM tags WHERE group_id = $i),
                       (SELECT count(*) FROM file_tags ft JOIN tags t ON t.id = ft.tag_id
                         WHERE t.group_id = $i)
                """, ("$i", id)))
            using (var r = cmd.ExecuteReader())
            {
                r.Read();
                tags = r.GetInt64(0);
                marks = r.GetInt64(1);
            }

            if (force != true && (tags > 0 || marks > 0))
                return Results.Ok(new { needConfirm = true, tags, marks });

            using var del = c.Sql("DELETE FROM tag_groups WHERE id = $i", ("$i", id));
            del.ExecuteNonQuery();
            return Results.Ok(new { ok = true, tags, marks });
        });

        // ── Теги ─────────────────────────────────────────────────────────

        app.MapPatch("/api/tags/{id:long}", (long id, TagPatch p) =>
        {
            using var c = repo.Open();

            // Имя и группа меняются вместе: перенос в чужую группу может
            // столкнуться с тамошним тёзкой, и проверять надо оба сразу.
            string? name = p.Name?.Trim();
            long? group = p.GroupId;

            if (name is { Length: 0 }) return Results.BadRequest(new { error = "пустое имя" });

            if (name is not null || group is not null)
            {
                using var cur = c.Sql("SELECT group_id, name FROM tags WHERE id = $i", ("$i", id));
                using var r = cur.ExecuteReader();
                if (!r.Read()) return Results.NotFound(new { error = "тега нет" });
                group ??= r.GetInt64(0);
                name ??= r.GetString(1);

                using var dup = c.Sql("""
                    SELECT id FROM tags
                    WHERE group_id = $g AND name = $n AND id <> $i AND ifnull(parent_id,0) = 0
                    """, ("$g", group.Value), ("$n", name), ("$i", id));
                var twin = dup.ScalarLong();
                if (twin > 0)
                    return Results.Ok(new { needMerge = true, into = twin, name });

                using var upd = c.Sql("UPDATE tags SET name = $n, group_id = $g WHERE id = $i",
                    ("$n", name), ("$g", group.Value), ("$i", id));
                upd.ExecuteNonQuery();
            }

            if (p.SortOrder is { } s)
            {
                using var cmd = c.Sql("UPDATE tags SET sort_order = $v WHERE id = $i", ("$v", s), ("$i", id));
                cmd.ExecuteNonQuery();
            }

            return Results.Ok(new { ok = true });
        });

        app.MapDelete("/api/tags/{id:long}", (long id, bool? force) =>
        {
            using var c = repo.Open();

            using var cnt = c.Sql("SELECT count(*) FROM file_tags WHERE tag_id = $i", ("$i", id));
            var marks = cnt.ScalarLong();
            if (force != true && marks > 0)
                return Results.Ok(new { needConfirm = true, marks });

            using var del = c.Sql("DELETE FROM tags WHERE id = $i", ("$i", id));
            del.ExecuteNonQuery();
            return Results.Ok(new { ok = true, marks });
        });

        // Объединение. Главный инструмент уборки: «Профориентация»
        // и «профориентация» рано или поздно заводятся сами.
        app.MapPost("/api/tags/{id:long}/merge", (long id, MergeCmd cmd) =>
        {
            if (cmd.IntoId == id) return Results.BadRequest(new { error = "тег сам в себя не сливается" });

            using var c = repo.Open();
            using var tx = c.BeginTransaction();

            // Файлы, у которых уже стоит целевой тег, вторым его не получат.
            using (var move = c.Sql("""
                INSERT OR IGNORE INTO file_tags (file_id, tag_id, source, added_at)
                SELECT file_id, $to, 'import', $now FROM file_tags WHERE tag_id = $from
                """, ("$to", cmd.IntoId), ("$from", id), ("$now", Repo.Now)))
                move.ExecuteNonQuery();

            long moved;
            using (var n = c.Sql("SELECT count(*) FROM file_tags WHERE tag_id = $from", ("$from", id)))
                moved = n.ScalarLong();

            // Вложенные теги не осиротеют: переезжают под цель.
            using (var kids = c.Sql("UPDATE tags SET parent_id = $to WHERE parent_id = $from",
                ("$to", cmd.IntoId), ("$from", id)))
                kids.ExecuteNonQuery();

            using (var del = c.Sql("DELETE FROM tags WHERE id = $from", ("$from", id)))
                del.ExecuteNonQuery();

            tx.Commit();
            return Results.Ok(new { ok = true, moved });
        });
    }
}

public sealed record GroupPatch(string? Name, bool? IsMulti, bool? IsRequired, int? SortOrder);
public sealed record TagPatch(string? Name, long? GroupId, int? SortOrder);
public sealed record MergeCmd(long IntoId);
