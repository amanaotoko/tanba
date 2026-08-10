using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Tanba.Storage;

namespace Tanba.Web;

/// <summary>
/// Библиотека: отбор разложенных файлов по тегам.
/// Отбор работает как запрос: теги равноправны, порядок ничего не значит.
/// </summary>
public static class Library
{
    /// Файлы из приёма ещё не разложены, в библиотеку они не попадают.
    private static readonly string InboxPrefix = Config.InboxName + @"\%";

    /// Условие, общее для списка, счётчика и фасетов.
    private const string FiledBase =
        "f.is_missing = 0 AND f.rel_path NOT LIKE $inbox AND " +
        "($q IS NULL OR f.id IN (SELECT rowid FROM files_fts WHERE files_fts MATCH $q))";

    /// <summary>
    /// Дата отбирается диапазоном, а не тегом: она непрерывна и уже лежит в files.
    /// Переключатель: создан / изменён / любая из двух.
    /// </summary>
    private static string Filed(string? dates)
    {
        static string Between(string col) =>
            $"({col} IS NOT NULL AND ($from IS NULL OR {col} >= $from) AND ($to IS NULL OR {col} <= $to))";

        var clause = dates switch
        {
            "created" => Between("f.file_ctime"),
            "modified" => Between("f.file_mtime"),
            // «и то и другое»: годится любая из дат, попавшая в диапазон.
            _ => $"({Between("f.file_ctime")} OR {Between("f.file_mtime")})",
        };

        // Диапазон не задан, условие не должно ничего отсекать.
        var byDate = $"(($from IS NULL AND $to IS NULL) OR {clause})";

        // Вход в каталог это область, а не условие отбора: он всегда И,
        // независимо от переключателя. Внутри видны прямые дети, вложенные
        // каталоги лежат каталогами, как в проводнике.
        const string inCatalog =
            "($cat IS NULL OR f.id IN (SELECT item_id FROM catalog_items WHERE catalog_id = $cat))";

        // Снаружи видны только самые верхние каталоги: вложенные это внутреннее
        // устройство работы, в общем списке им делать нечего. Файлы видны всегда,
        // каталог их не прячет.
        const string topOnly =
            "($cat IS NOT NULL OR f.kind <> 'catalog' " +
            "OR NOT EXISTS (SELECT 1 FROM catalog_items ci WHERE ci.item_id = f.id))";

        // Группа «Показать»: файлы, каталоги или и то и другое. Это не формат
        // и не тег, а вид объекта, поэтому живёт отдельной группой и всегда И.
        // Внутри каталога не действует: там видно всё, что лежит.
        const string byKind =
            "($cat IS NOT NULL OR ((f.kind = 'file' AND $showFiles = 1) " +
            "OR (f.kind = 'catalog' AND $showCats = 1)))";

        return $"{FiledBase} AND {byDate} AND {inCatalog} AND {topOnly} AND {byKind}";
    }

    /// <summary>
    /// «файлы», «каталоги» или оба. Пусто означает оба: снять обе галочки
    /// интерфейс не даёт, но запрос может прийти каким угодно.
    /// </summary>
    private static (bool Files, bool Cats) ParseShow(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return (true, true);
        var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var f = parts.Contains("files");
        var c = parts.Contains("catalogs");
        return f || c ? (f, c) : (true, true);
    }

    /// <summary>«2026-08-01» или unix-секунды. Конец дня включаем целиком.</summary>
    private static long? ParseDate(string? s, bool endOfDay)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (long.TryParse(s, out var unix)) return unix;
        if (!DateTime.TryParse(s, out var d)) return null;
        if (endOfDay) d = d.Date.AddDays(1).AddSeconds(-1);
        return new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d)).ToUnixTimeSeconds();
    }

    public static void MapLibrary(this WebApplication app, Repo repo, Config cfg)
    {
        // ── Отбор ────────────────────────────────────────────────────────

        app.MapGet("/api/library", (string? tags, string? mode, string? q, int? limit, int? offset,
                                    string? from, string? to, string? dates, string? ext, long? cat, string? show) =>
        {
            var tagIds = ParseIds(tags);
            var exts = ParseExts(ext);
            var and = IsAnd(mode);
            var take = Math.Clamp(limit ?? 200, 1, 1000);
            var skip = Math.Max(offset ?? 0, 0);
            var (f1, f2) = (ParseDate(from, false), ParseDate(to, true));
            var kinds = ParseShow(show);
            var ep = ExtParams(exts);

            using var c = repo.Open();
            var sel = SelectionCte(tagIds, and, dates, exts.Length);

            long total, bytes;
            using (var cmd = Query(c, $"""
                {sel}
                SELECT count(*), ifnull(sum(f.size), 0)
                FROM files f JOIN sel ON sel.id = f.id
                """, q, f1, f2, cat, kinds, ep))
            using (var r = cmd.ExecuteReader())
            {
                r.Read();
                total = r.GetInt64(0);
                bytes = r.GetInt64(1);
            }

            var rows = new List<Row>();
            using (var cmd = Query(c, $"""
                {sel}
                SELECT f.id, f.orig_name, f.ext, f.size, f.added_at, f.file_ctime, f.file_mtime,
                       f.kind, f.cover_file_id,
                       (SELECT count(*) FROM catalog_items ci WHERE ci.catalog_id = f.id)
                FROM files f JOIN sel ON sel.id = f.id
                ORDER BY CASE WHEN f.kind = 'catalog' THEN 0 ELSE 1 END, f.added_at DESC, f.id DESC
                LIMIT $take OFFSET $skip
                """, q, f1, f2, cat, kinds, [.. ep, ("$take", (object?)take), ("$skip", skip)]))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    rows.Add(new Row(r.GetInt64(0), r.GetString(1), r.Str(2), r.Num(3), r.GetInt64(4),
                        r.Num(5), r.Num(6), r.GetString(7), r.Num(8), r.GetInt64(9)));

            var perFile = TagsOfMany(c, rows.Select(x => x.Id).ToList());

            return Results.Ok(new
            {
                total,
                bytes,
                limit = take,
                offset = skip,
                files = rows.Select(x => new
                {
                    id = x.Id,
                    name = x.Name,
                    ext = x.Ext,
                    size = x.Size,
                    addedAt = x.AddedAt,
                    createdAt = x.Ctime,
                    modifiedAt = x.Mtime,
                    // Каталог приезжает той же карточкой, но интерфейс должен
                    // отличать его: двойной клик по нему входит внутрь.
                    kind = x.Kind,
                    coverId = x.CoverId,
                    count = x.Kind == "catalog" ? x.Count : (long?)null,
                    tags = perFile.GetValueOrDefault(x.Id, []),
                }),
            });
        });

        // ── Фасеты: во что превратится отбор, если дописать в него тег ────

        app.MapGet("/api/library/facets", (string? tags, string? mode, string? q,
                                           string? from, string? to, string? dates, string? ext, long? cat, string? show) =>
        {
            var tagIds = ParseIds(tags);
            var exts = ParseExts(ext);
            var and = IsAnd(mode);
            // Пустой отбор ведёт себя как И: добавить условие = взять файлы этого условия.
            var narrowing = and || (tagIds.Length == 0 && exts.Length == 0);
            var (f1, f2) = (ParseDate(from, false), ParseDate(to, true));
            var kinds = ParseShow(show);
            var ep = ExtParams(exts);

            using var c = repo.Open();
            var sel = SelectionCte(tagIds, and, dates, exts.Length);

            long total;
            using (var cmd = Query(c, $"{sel} SELECT count(*) FROM sel", q, f1, f2, cat, kinds, ep))
                total = cmd.ScalarLong();

            // При И отбор сужается, считаем пересечение с найденным.
            // При ИЛИ расширяется, считаем, сколько файлов тега лежит вне найденного.
            var counts = new Dictionary<long, long>();
            using (var cmd = Query(c, narrowing
                ? $"""
                   {sel}
                   SELECT ft.tag_id, count(*)
                   FROM file_tags ft JOIN sel ON sel.id = ft.file_id
                   GROUP BY ft.tag_id
                   """
                : $"""
                   {sel}
                   SELECT ft.tag_id, count(*)
                   FROM file_tags ft JOIN files f ON f.id = ft.file_id
                   WHERE {Filed(dates)} AND ft.file_id NOT IN (SELECT id FROM sel)
                   GROUP BY ft.tag_id
                   """, q, f1, f2, cat, kinds, ep))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) counts[r.GetInt64(0)] = r.GetInt64(1);

            // Счётчики формата считаются по отбору БЕЗ самого формата.
            // Иначе выходит вранье: раз внутри группы ИЛИ, добавление второго
            // расширения выдачу расширяет, а не сужает, и рядом с png должно
            // стоять «сколько прибавится», а не «сколько осталось».
            var selNoExt = SelectionCte(tagIds, and, dates, 0);
            var fmtNarrowing = and || tagIds.Length == 0;

            var extCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = Query(c, fmtNarrowing
                ? $"""
                   {selNoExt}
                   SELECT f.ext, count(*)
                   FROM files f JOIN sel ON sel.id = f.id
                   WHERE f.ext IS NOT NULL AND f.ext <> ''
                   GROUP BY f.ext
                   """
                : $"""
                   {selNoExt}
                   SELECT f.ext, count(*)
                   FROM files f
                   WHERE {Filed(dates)} AND f.ext IS NOT NULL AND f.ext <> ''
                     AND f.id NOT IN (SELECT id FROM sel)
                   GROUP BY f.ext
                   """, q, f1, f2, cat, kinds))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) extCounts[r.GetString(0)] = r.GetInt64(1);

            // «Показать» считается по отбору без учёта самой себя: рядом
            // с «каталоги» должно стоять, сколько их всего в этом отборе,
            // а не сколько осталось после того, как их уже отключили.
            long nFiles = 0, nCats = 0;
            using (var cmd = Query(c, $"""
                {SelectionCte(tagIds, and, dates, exts.Length)}
                SELECT sum(f.kind = 'file'), sum(f.kind = 'catalog')
                FROM files f JOIN sel ON sel.id = f.id
                """, q, f1, f2, cat, (true, true), ep))
            using (var r = cmd.ExecuteReader())
                if (r.Read()) { nFiles = r.Num(0) ?? 0; nCats = r.Num(1) ?? 0; }

            return Results.Ok(new
            {
                total,
                root = cfg.Root,
                groups = GroupsWithCounts(c, counts, narrowing ? 0 : total),
                formats = FormatGroup(c, extCounts, 0),
                kinds = new
                {
                    name = "Показать",
                    items = new[]
                    {
                        new { key = "files", name = "файлы", count = nFiles },
                        new { key = "catalogs", name = "каталоги", count = nCats },
                    },
                },
            });
        });

        // ── Сохранённые отборы ───────────────────────────────────────────

        app.MapGet("/api/library/saved", () =>
        {
            using var c = repo.Open();
            var list = new List<object>();
            using var cmd = c.Sql("SELECT id, name, mode, tag_ids, ext_list, created_at FROM saved_queries ORDER BY sort_order, id");
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new
                {
                    id = r.GetInt64(0),
                    name = r.GetString(1),
                    mode = r.GetString(2),
                    tagIds = ParseJsonIds(r.GetString(3)),
                    exts = ParseJsonStrings(r.Str(4)),
                    createdAt = r.GetInt64(5),
                });
            return Results.Ok(list);
        });

        app.MapPost("/api/library/saved", (SavedCmd cmd) =>
        {
            var name = (cmd.Name ?? "").Trim();
            if (name.Length == 0) return Results.BadRequest(new { error = "пустое имя" });

            var ids = (cmd.TagIds ?? []).Where(x => x > 0).Distinct().ToArray();

            var exts = ParseExts(string.Join(',', cmd.Exts ?? []));

            using var c = repo.Open();
            // Отбор с тем же именем перезаписываем: «Сохранить» дважды означает правку.
            using var ins = c.Sql("""
                INSERT INTO saved_queries (name, mode, tag_ids, ext_list, sort_order, created_at)
                VALUES ($n, $m, $t, $e, (SELECT ifnull(max(sort_order), 0) + 1 FROM saved_queries), $at)
                ON CONFLICT(name) DO UPDATE SET
                    mode = excluded.mode, tag_ids = excluded.tag_ids, ext_list = excluded.ext_list;
                SELECT id FROM saved_queries WHERE name = $n;
                """,
                ("$n", name), ("$m", IsAnd(cmd.Mode) ? "and" : "or"),
                ("$t", JsonSerializer.Serialize(ids)),
                ("$e", JsonSerializer.Serialize(exts)), ("$at", Repo.Now));
            return Results.Ok(new { id = ins.ScalarLong(), name });
        });

        app.MapDelete("/api/library/saved/{id:long}", (long id) =>
        {
            using var c = repo.Open();
            using var cmd = c.Sql("DELETE FROM saved_queries WHERE id = $i", ("$i", id));
            cmd.ExecuteNonQuery();
            return Results.Ok(new { ok = true });
        });

        // ── Отдать файл обратно ──────────────────────────────────────────
        // Каталог нужен ради этого: нашёл и сразу к файлу на диске.

        app.MapPost("/api/library/reveal", (RevealCmd cmd) =>
        {
            using var c = repo.Open();
            using var find = c.Sql("SELECT rel_path, kind FROM files WHERE id = $i", ("$i", cmd.Id));
            using var r = find.ExecuteReader();
            if (!r.Read()) return Results.NotFound(new { error = "объекта нет в библиотеке" });

            // У каталога байтов на диске нет: показывать в проводнике нечего,
            // двойной клик по нему должен входить внутрь.
            if (r.GetString(1) == "catalog")
                return Results.BadRequest(new { error = "каталог живёт только в программе, на диске его нет" });

            var full = cfg.ToFull(r.GetString(0));
            if (!File.Exists(full) && !Directory.Exists(full))
                return Results.NotFound(new { error = "файл пропал с диска" });

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = true });
            return Results.Ok(new { ok = true });
        });
    }

    // ── Сборка запроса ───────────────────────────────────────────────────

    /// <summary>
    /// CTE «sel» собирает id, попавшие в отбор. Одна и та же выборка нужна
    /// и списку, и счётчику, и фасетам, поэтому собирается в одном месте.
    /// </summary>
    private static string SelectionCte(long[] tagIds, bool and, string? dates = null, int extCount = 0)
    {
        if (tagIds.Length == 0 && extCount == 0)
            return $"WITH sel AS (SELECT f.id AS id FROM files f WHERE {Filed(dates)})";

        // Теги и формат сводятся в один список условий, дальше работает общая
        // арифметика: при И должны совпасть все, при ИЛИ хватит любого.
        //
        // Формат при этом даёт ОДНО условие на всю группу, а не по одному на
        // расширение. Причина в том, что ext у файла ровно один: если считать
        // каждое расширение отдельным условием, то «jpg И png» не совпадёт
        // никогда, при любых данных. Поэтому внутри группы всегда ИЛИ,
        // а с тегами группа соединяется уже по общему переключателю.
        var parts = new List<string>();

        if (tagIds.Length > 0)
            parts.Add($"""
                  SELECT f.id AS id, 'tag:' || t.id AS crit
                  FROM files f
                  JOIN file_tags ft ON ft.file_id = f.id
                  JOIN tags t ON t.id = ft.tag_id
                  WHERE {Filed(dates)} AND t.id IN ({string.Join(',', tagIds)})
                """);

        if (extCount > 0)
        {
            var slots = string.Join(',', Enumerable.Range(0, extCount).Select(i => $"$e{i}"));
            parts.Add($"""
                  SELECT f.id AS id, 'ext' AS crit
                  FROM files f
                  WHERE {Filed(dates)} AND f.ext IN ({slots})
                """);
        }

        var total = tagIds.Length + (extCount > 0 ? 1 : 0);
        var body = and
            ? $"SELECT id FROM crit GROUP BY id HAVING count(DISTINCT crit) = {total}"
            : "SELECT DISTINCT id FROM crit";

        return $"""
            WITH crit AS (
            {string.Join("\n  UNION\n", parts)}
            ),
            sel AS ({body})
            """;
    }


    /// <summary>Список расширений из строки запроса. Приводим к нижнему регистру, как в базе.</summary>
    private static string[] ParseExts(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : [.. s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Select(x => x.TrimStart('.').ToLowerInvariant())
                   .Where(x => x.Length is > 0 and <= 20)
                   .Distinct()];

    /// <summary>Расширения уходят параметрами: в имени формата может оказаться что угодно.</summary>
    private static (string, object?)[] ExtParams(string[] exts) =>
        [.. exts.Select((e, i) => ($"$e{i}", (object?)e))];

    /// <summary>Команда с параметрами, которые нужны каждому запросу отбора.</summary>
    private static SqliteCommand Query(SqliteConnection c, string sql, string? q,
        long? from = null, long? to = null, long? cat = null,
        (bool Files, bool Cats) show = default, params (string, object?)[] more)
    {
        var needle = Fts(q);
        var (sf, sc) = show == default ? (true, true) : show;
        return c.Sql(sql, [
            ("$inbox", (object?)InboxPrefix), ("$q", needle),
            ("$from", from), ("$to", to), ("$cat", cat),
            ("$showFiles", sf ? 1 : 0), ("$showCats", sc ? 1 : 0), .. more]);
    }

    /// <summary>
    /// Превращает набранное человеком в запрос FTS5.
    ///
    /// Режем ввод там же, где режет токенизатор, по всему, что не буква и
    /// не цифра: тогда «img9.jpg» ищется двумя словами и находится, а не
    /// превращается в несуществующее «img9jpg». Каждое слово берём в кавычки,
    /// иначе минус или скобка во вводе прочитаются как синтаксис запроса
    /// и SQLite ответит ошибкой прямо человеку в лицо.
    ///
    /// Звёздочка разрешает продолжение слова: «рекл» находит «Реклама».
    /// Слова соединяются через И, но порядок их не важен, поэтому
    /// «унив реклама» находит «Реклама универа».
    /// </summary>
    private static string? Fts(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var words = Regex.Split(text, @"[^\p{L}\p{N}]+")
            .Where(w => w.Length > 0)
            .Select(w => '"' + w + "\"*");

        var q = string.Join(' ', words);
        return q.Length == 0 ? null : q;
    }

    // ── Данные для панели ────────────────────────────────────────────────

    /// <summary>
    /// Группы тегов со счётчиками фасетов. Все группы устроены одинаково,
    /// разница только в том, можно ли выбрать несколько тегов сразу.
    /// </summary>
    private static object[] GroupsWithCounts(SqliteConnection c, Dictionary<long, long> counts, long baseline)
    {
        var byGroup = new Dictionary<long, List<object>>();
        using (var cmd = c.Sql("SELECT id, group_id, parent_id, name FROM tags ORDER BY sort_order, name"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var gid = r.GetInt64(1);
                if (!byGroup.TryGetValue(gid, out var l)) byGroup[gid] = l = [];
                var id = r.GetInt64(0);
                l.Add(new
                {
                    id,
                    parentId = r.Num(2),
                    name = r.GetString(3),
                    count = baseline + counts.GetValueOrDefault(id),
                });
            }

        var groups = new List<object>();
        using (var cmd = c.Sql("SELECT id, name, is_multi FROM tag_groups ORDER BY sort_order, name"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var id = r.GetInt64(0);
                groups.Add(new
                {
                    id,
                    name = r.GetString(1),
                    isMulti = r.GetInt64(2) != 0,
                    tags = byGroup.GetValueOrDefault(id, []),
                });
            }
        return [.. groups];
    }

    /// <summary>
    /// Группа «Формат». Устроена как обычная группа тегов и слушается того же И/ИЛИ,
    /// поэтому в интерфейсе рисуется тем же блоком. В списке только те расширения,
    /// которые в библиотеке реально есть: показывать пустые незачем.
    /// </summary>
    private static object FormatGroup(SqliteConnection c, Dictionary<string, long> counts, long baseline)
    {
        var items = new List<object>();

        using (var cmd = c.Sql("""
            SELECT DISTINCT ext FROM files
            WHERE is_missing = 0 AND ext IS NOT NULL AND ext <> ''
              AND rel_path NOT LIKE $pfx
            ORDER BY ext
            """, ("$pfx", InboxPrefix)))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var e = r.GetString(0);
                items.Add(new { ext = e, name = e, count = baseline + counts.GetValueOrDefault(e) });
            }

        return new { name = "Формат", isMulti = true, items };
    }

    /// <summary>Теги для целой страницы результатов одним запросом.</summary>
    private static Dictionary<long, List<long>> TagsOfMany(SqliteConnection c, List<long> fileIds)
    {
        var map = new Dictionary<long, List<long>>();
        if (fileIds.Count == 0) return map;

        using var cmd = c.Sql($"""
            SELECT ft.file_id, ft.tag_id
            FROM file_tags ft JOIN tags t ON t.id = ft.tag_id
            WHERE ft.file_id IN ({string.Join(',', fileIds)})
            ORDER BY t.sort_order, t.name
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var f = r.GetInt64(0);
            if (!map.TryGetValue(f, out var l)) map[f] = l = [];
            l.Add(r.GetInt64(1));
        }
        return map;
    }

    // ── Разбор параметров ────────────────────────────────────────────────

    private static bool IsAnd(string? mode) =>
        !string.Equals(mode, "or", StringComparison.OrdinalIgnoreCase);

    private static long[] ParseIds(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Select(x => long.TryParse(x, out var v) ? v : 0)
               .Where(v => v > 0)
               .Distinct()
               .ToArray();

    private static long[] ParseJsonIds(string json)
    {
        try { return JsonSerializer.Deserialize<long[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>Отборы, сохранённые до появления формата, лежат без ext_list.</summary>
    private static string[] ParseJsonStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}

/// <summary>Строка выдачи. Каталог приезжает здесь же, отличается полем Kind.</summary>
internal sealed record Row(
    long Id, string Name, string? Ext, long? Size, long AddedAt,
    long? Ctime, long? Mtime, string Kind, long? CoverId, long Count);

public sealed record SavedCmd(string? Name, string? Mode, long[]? TagIds, string[]? Exts);
public sealed record RevealCmd(long Id);
