using System.Text;
using Microsoft.Data.Sqlite;

namespace Tanba.Storage;

/// <summary>
/// Страховка на случай, если база однажды не откроется.
/// catalog.csv это плоская выгрузка «где лежит → чем размечено», читается блокнотом и экселем.
/// Плюс ежедневная копия самой базы.
/// </summary>
public sealed class Safety(Config cfg, Repo repo)
{
    /// <summary>Перевыгружает каталог целиком. Зовётся после каждого разбора.</summary>
    public void ExportCatalog()
    {
        using var c = repo.Open();

        var groups = repo.Groups(c).Select(g => g.Name).ToList();
        var sb = new StringBuilder();

        // Разделителем берём точку с запятой: так эксель в русской локали открывает без плясок.
        sb.Append("путь;имя;размер;sha256;создан;изменён;добавлен");
        foreach (var g in groups) sb.Append(';').Append(Esc(g));
        sb.AppendLine();

        using var cmd = c.Sql($"""
            SELECT f.rel_path, f.orig_name, f.size, f.sha256,
                   f.file_ctime, f.file_mtime, f.added_at,
                   (SELECT group_concat(gr.name || char(31) || t.name, char(30))
                      FROM file_tags ft
                      JOIN tags t ON t.id = ft.tag_id
                      JOIN tag_groups gr ON gr.id = t.group_id
                     WHERE ft.file_id = f.id)
            FROM files f
            WHERE f.is_missing = 0 AND f.rel_path NOT LIKE $pfx
            ORDER BY f.id
            """, ("$pfx", Config.InboxName + @"\%"));

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            // Теги приезжают одной строкой: «Группа␟Тег␞Группа␟Тег», раскладываем по колонкам.
            var byGroup = groups.ToDictionary(g => g, _ => new List<string>());
            var packed = r.Str(7);
            if (packed is not null)
                foreach (var pair in packed.Split('', StringSplitOptions.RemoveEmptyEntries))
                {
                    var i = pair.IndexOf('');
                    if (i < 0) continue;
                    var g = pair[..i];
                    if (byGroup.TryGetValue(g, out var list)) list.Add(pair[(i + 1)..]);
                }

            sb.Append(Esc(r.GetString(0))).Append(';')
              .Append(Esc(r.GetString(1))).Append(';')
              .Append(r.Num(2)?.ToString() ?? "").Append(';')
              .Append(r.Str(3) ?? "").Append(';')
              .Append(Date(r.Num(4))).Append(';')
              .Append(Date(r.Num(5))).Append(';')
              .Append(Date(r.Num(6)));

            foreach (var g in groups) sb.Append(';').Append(Esc(string.Join(", ", byGroup[g])));
            sb.AppendLine();
        }

        // BOM обязателен, иначе эксель покажет кириллицу кракозябрами.
        Directory.CreateDirectory(cfg.Meta);
        System.IO.File.WriteAllText(cfg.CatalogCsv, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Копия базы раз в сутки. Старше двух недель удаляем.</summary>
    public void BackupDaily()
    {
        Directory.CreateDirectory(cfg.BackupDir);
        var name = $"tanba-{DateTime.Now:yyyy-MM-dd}.db";
        var dst = Path.Combine(cfg.BackupDir, name);
        if (System.IO.File.Exists(dst)) return;

        // Через VACUUM INTO, а не копированием файла: WAL может быть недописан.
        using var c = repo.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "VACUUM INTO $p";
        cmd.Parameters.AddWithValue("$p", dst);
        cmd.ExecuteNonQuery();

        var cutoff = DateTime.Now.AddDays(-14);
        foreach (var old in Directory.EnumerateFiles(cfg.BackupDir, "tanba-*.db"))
            if (System.IO.File.GetLastWriteTime(old) < cutoff)
                try { System.IO.File.Delete(old); } catch (IOException) { }
    }

    private static string Date(long? unix) =>
        unix is null or 0 ? "" : DateTimeOffset.FromUnixTimeSeconds(unix.Value).LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    private static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Contains(';') || s.Contains('"') || s.Contains('\n')
            ? '"' + s.Replace("\"", "\"\"") + '"'
            : s;
    }
}
