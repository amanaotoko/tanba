using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Tanba.Storage;

namespace Tanba.Web;

/// <summary>
/// Действия над файлом из контекстного меню: открыть, переименовать,
/// удалить в корзину, посмотреть теги выделенного.
/// </summary>
public static class Files
{
    public static void MapFiles(this WebApplication app, Repo repo, Config cfg, Tanba.Shell.Thumbs thumbs)
    {
        // ── Открыть в программе по умолчанию ─────────────────────────────

        app.MapPost("/api/files/open", (IdCmd cmd) =>
        {
            using var c = repo.Open();
            var f = repo.Get(c, cmd.Id);
            if (f is null) return Results.NotFound(new { error = "объекта нет в библиотеке" });

            var full = cfg.ToFull(f.RelPath);
            if (!System.IO.File.Exists(full))
                return Results.BadRequest(new { error = "файла нет на диске" });

            Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
            return Results.Ok(new { ok = true });
        });

        // ── Переименовать ────────────────────────────────────────────────

        app.MapPost("/api/files/rename", (RenameCmd cmd) =>
        {
            var raw = (cmd.Name ?? "").Trim();
            if (raw.Length == 0) return Results.BadRequest(new { error = "пустое имя" });

            using var c = repo.Open();
            var f = repo.Get(c, cmd.Id);
            if (f is null) return Results.NotFound(new { error = "объекта нет в библиотеке" });

            // Расширение решает программа, а не человек: перепечатать его руками
            // означает однажды превратить psd в ps и потерять открывалку.
            var ext = Path.GetExtension(f.OrigName);
            var stem = Path.GetFileNameWithoutExtension(Tanba.Shell.Native.SafeName(raw));
            if (stem.Length == 0) return Results.BadRequest(new { error = "имя состоит из недопустимых знаков" });
            var newName = stem + ext;

            // Имя на диске меняем тоже, иначе через год в _store будут лежать
            // имена, не совпадающие ни с чем. Номер в начале не трогаем:
            // именно он, а не текст после него, задаёт идентичность файла.
            var oldFull = cfg.ToFull(f.RelPath);
            var newRel = f.RelPath;

            if (System.IO.File.Exists(oldFull))
            {
                var dir = Path.GetDirectoryName(oldFull)!;
                var newFull = Path.Combine(dir, $"{cmd.Id:D6}__{newName}");
                if (!string.Equals(oldFull, newFull, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        System.IO.File.Move(oldFull, newFull);
                        newRel = cfg.ToRelative(newFull);
                    }
                    catch (IOException)
                    {
                        // Файл занят, например открыт в Corel. Имя в программе
                        // меняем сейчас, диск догоним при следующем переименовании.
                    }
                }
            }

            using var upd = c.Sql(
                "UPDATE files SET orig_name = $n, rel_path = $p WHERE id = $i",
                ("$n", newName), ("$p", newRel), ("$i", cmd.Id));
            upd.ExecuteNonQuery();
            Repo.Index(c, cmd.Id, newName);

            repo.LogEvent(c, "rename", cmd.Id, null, newName);
            return Results.Ok(new { id = cmd.Id, name = newName, onDisk = newRel != f.RelPath });
        });

        // ── Удалить в корзину ────────────────────────────────────────────

        app.MapPost("/api/files/delete", (IdsCmd cmd) =>
        {
            var ids = (cmd.Ids ?? []).Where(x => x > 0).Distinct().ToArray();
            if (ids.Length == 0) return Results.BadRequest(new { error = "нечего удалять" });

            int gone = 0;
            var errors = new List<string>();
            using var c = repo.Open();

            foreach (var id in ids)
            {
                var f = repo.Get(c, id);
                if (f is null) continue;

                // Каталог сюда попадает вместе со смешанным выделением: правое
                // меню выбирается по тому, на чём щёлкнули, а список уходит весь.
                // На диске у каталога ничего нет, поэтому в корзину не уйдёт
                // ничего, а пометка пропавшим убрала бы его навсегда: возврат
                // умеет работать только с файлами.
                using (var kind = c.Sql("SELECT kind FROM files WHERE id = $i", ("$i", id)))
                    if (Convert.ToString(kind.ExecuteScalar()) != "file")
                    {
                        errors.Add($"{f.OrigName}: это каталог, удаляется отдельно");
                        continue;
                    }

                var full = cfg.ToFull(f.RelPath);
                try
                {
                    if (System.IO.File.Exists(full)) Recycle.Send(full);

                    // Строку не удаляем, а помечаем пропавшей. Корзина возвращает
                    // файл на прежнее место, и тогда он вернётся вместе с тегами,
                    // сам по себе, без единого действия.
                    using var upd = c.Sql("UPDATE files SET is_missing = 1 WHERE id = $i", ("$i", id));
                    upd.ExecuteNonQuery();

                    thumbs.Forget(id);
                    repo.LogEvent(c, "deleted", id, null, f.OrigName);
                    gone++;
                }
                catch (Exception ex) { errors.Add($"{f.OrigName}: {ex.Message}"); }
            }

            return Results.Ok(new { deleted = gone, errors });
        });

        // ── Вернулись ли пропавшие ───────────────────────────────────────
        // Достали из корзины: файл лёг на прежнее место, значит снимаем метку.
        // Сканер сюда не ходит, он обходит только приём.

        app.MapPost("/api/files/recheck", () =>
        {
            using var c = repo.Open();
            var back = new List<long>();

            using (var cmd = c.Sql("SELECT id, rel_path FROM files WHERE is_missing = 1 AND kind = 'file'"))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    if (System.IO.File.Exists(cfg.ToFull(r.GetString(1)))) back.Add(r.GetInt64(0));

            foreach (var id in back)
            {
                using var upd = c.Sql("UPDATE files SET is_missing = 0 WHERE id = $i", ("$i", id));
                upd.ExecuteNonQuery();
                repo.LogEvent(c, "restored", id, null, null);
            }

            return Results.Ok(new { restored = back.Count });
        });

        // ── Теги выделенного ─────────────────────────────────────────────
        // Те же группы, что в панели разбора, но для произвольного набора
        // файлов: модалка правого клика работает на всём выделении.

        app.MapGet("/api/files/tagstates", (string? ids) =>
        {
            var fileIds = ParseIds(ids);
            using var c = repo.Open();
            var groups = repo.Groups(c);
            var states = repo.TagStates(c, fileIds);

            return Results.Ok(new
            {
                count = fileIds.Length,
                groups = groups.Select(g => new
                {
                    id = g.Id,
                    name = g.Name,
                    isMulti = g.IsMulti,
                    tags = g.Tags.Select(t => new
                    {
                        id = t.Id,
                        name = t.Name,
                        state = states.TryGetValue(t.Id, out var s) ? s.ToString().ToLowerInvariant() : "off",
                    }),
                }),
            });
        });
    }

    private static long[] ParseIds(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : [.. s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Where(x => long.TryParse(x, out _)).Select(long.Parse)];
}

public sealed record IdCmd(long Id);
public sealed record IdsCmd(long[]? Ids);
public sealed record RenameCmd(long Id, string? Name);
