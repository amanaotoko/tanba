using System.IO.Enumeration;
using System.Security.Cryptography;
using Tanba.Storage;

namespace Tanba.Scanner;

/// <summary>
/// Приём файлов: обход папки «Inbox» и операция «Разложить».
/// Единственное место, которое двигает файлы по диску.
/// </summary>
public sealed class Ingest(Config cfg, Repo repo, Tanba.Shell.Thumbs? thumbs = null)
{
    /// <summary>
    /// Пересматривает папку приёма и приводит базу в соответствие с диском.
    /// Возвращает, сколько файлов сейчас ждут разбора.
    /// </summary>
    public int ScanInbox()
    {
        Directory.CreateDirectory(cfg.Inbox);
        using var c = repo.Open();
        var rules = repo.IgnoreRules(c);
        var seen = new List<string>();

        foreach (var full in Directory.EnumerateFiles(cfg.Inbox, "*", SearchOption.AllDirectories))
        {
            var rel = cfg.ToRelative(full);

            // Мусор не каталогизируем, но и не удаляем: убираем с глаз,
            // чтобы приём мог стать по-настоящему пустым.
            var rule = MatchRule(rel, rules);
            if (rule is not null)
            {
                Quarantine(full, rule.Action == "version" ? cfg.VersionsInbox : cfg.Junk);
                continue;
            }

            // Дальше идут три причины пройти мимо файла, и во всех трёх он
            // остаётся в списке увиденных. «Не смог прочитать» и «его нет»
            // это разные вещи: раньше они шли одним путём, и файл, открытый
            // в другой программе, терял строку вместе со всеми тегами.
            FileInfo fi;
            try { fi = new FileInfo(full); if (!fi.Exists) continue; }
            catch (IOException) { seen.Add(rel); continue; }

            // Файл ещё копируется, поймаем на следующем проходе.
            if (IsLocked(full)) { seen.Add(rel); continue; }

            string sha;
            try { sha = Sha256(full); }
            catch (IOException) { seen.Add(rel); continue; }

            // NTFS переживает перенос внутри тома, поэтому дата создания
            // после «Разложить» остаётся настоящей, а не датой попадания в хранилище.
            repo.UpsertInbox(c, rel, fi.Name, fi.Extension.TrimStart('.').ToLowerInvariant(),
                fi.Length, sha,
                new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds(),
                new DateTimeOffset(fi.CreationTimeUtc).ToUnixTimeSeconds());
            seen.Add(rel);
        }

        // Вторая защита, на случай если появится ещё один путь «пройти мимо»:
        // строку сносим только когда файла действительно нет на диске.
        foreach (var id in repo.ForgetMissingInbox(c, seen, rel => System.IO.File.Exists(cfg.ToFull(rel))))
            thumbs?.Forget(id);

        return seen.Count;
    }

    /// <summary>
    /// «Разложить»: переносит размеченные файлы из приёма в хранилище.
    /// Файлы без единого тега не трогает: их надо либо разметить, либо отложить.
    /// </summary>
    public FileResult File(IReadOnlyList<long> fileIds, bool allowUntagged = false)
    {
        int moved = 0, merged = 0, skipped = 0;
        var errors = new List<string>();

        using var c = repo.Open();
        foreach (var id in fileIds)
        {
            var f = repo.Get(c, id);
            if (f is null || !f.Pending) continue;

            if (!allowUntagged && repo.TagsOf(c, id).Count == 0) { skipped++; continue; }

            var src = cfg.ToFull(f.RelPath);
            if (!System.IO.File.Exists(src)) { repo.Delete(c, id); thumbs?.Forget(id); continue; }

            try
            {
                // Такое содержимое уже разложено, копию не плодим и сливаем теги.
                if (f.Sha256 is not null)
                {
                    var twin = repo.FindFiledByHash(c, f.Sha256, id);

                    // Выжившего проверяем на диске, а не по колонке is_missing:
                    // она хранит мнение базы, а мнение устаревает. Файл могли
                    // удалить в проводнике, и тогда «дубликат» это последняя
                    // копия, а слияние стёрло бы её насовсем.
                    if (twin > 0 && !System.IO.File.Exists(cfg.ToFull(repo.Get(c, twin)?.RelPath ?? "")))
                    {
                        repo.MarkMissing(c, twin);
                        twin = 0;
                    }

                    if (twin > 0)
                    {
                        var keeper = repo.Get(c, twin);
                        using var tx = c.BeginTransaction();
                        repo.MergeTags(c, id, twin);
                        // Победил тот, кого разложили раньше, но имя берём осмысленное:
                        // «промо.mp4» лучше, чем «промо (копия).mp4».
                        if (keeper is not null && BetterName(f.OrigName, keeper.OrigName) == f.OrigName)
                            repo.SetOrigName(c, twin, f.OrigName);
                        repo.Delete(c, id);
                        repo.LogEvent(c, "dup_merged", twin, null, f.OrigName);
                        tx.Commit();

                        // В корзину, а не насовсем. Это единственное место
                        // в программе, которое расстаётся с байтами, и у него
                        // нет причин быть строже кнопки «Удалить».
                        Tanba.Web.Recycle.Send(src);
                        thumbs?.Forget(id);   // id исчез, эскиз в кэше не нужен
                        merged++;
                        continue;
                    }
                }

                var now = DateTimeOffset.Now;
                var dir = Path.Combine(cfg.Store, now.ToString("yyyy"), now.ToString("MM"));
                Directory.CreateDirectory(dir);

                var dst = Unique(Path.Combine(dir, $"{id:D6}__{f.OrigName}"));
                System.IO.File.Move(src, dst);

                repo.SetRelPath(c, id, cfg.ToRelative(dst));

                // Номер записываем сразу, а не ждём ближайшего обхода: между
                // раскладыванием и обходом файл нечем было бы опознать, а
                // переложить его могут ровно в эту минуту.
                repo.SetFileId(c, id, Shell.Native.FileId(dst));

                repo.LogEvent(c, "filed", id, null, f.OrigName);
                moved++;
            }
            catch (Exception ex)
            {
                errors.Add($"{f.OrigName}: {ex.Message}");
            }
        }

        CleanEmptyDirs(cfg.Inbox);
        return new FileResult(moved, merged, 0, skipped, errors);
    }

    // ── вспомогательное ──────────────────────────────────────────────────

    /// <summary>Первое подошедшее правило из ignore_rules, либо null.</summary>
    private static IgnoreRule? MatchRule(string relPath, List<IgnoreRule> rules)
    {
        var name = Path.GetFileName(relPath);
        var parts = relPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        foreach (var r in rules)
        {
            // Шаблон с обратным слэшем на конце задаёт имя папки целиком.
            if (r.Pattern.EndsWith('\\'))
            {
                var folder = r.Pattern.TrimEnd('\\');
                if (parts.Any(p => p.Equals(folder, StringComparison.OrdinalIgnoreCase))) return r;
                continue;
            }
            if (FileSystemName.MatchesSimpleExpression(r.Pattern, name, ignoreCase: true)) return r;
        }
        return null;
    }

    /// <summary>
    /// Какое из двух имён показывать пользователю после слияния дубликатов.
    /// Путь на диске при этом не меняется, он остаётся идентичностью файла.
    /// </summary>
    private static string BetterName(string a, string b)
    {
        static bool Junk(string s) =>
            s.Contains("копия", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("copy", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("(1)") || s.Contains("(2)");

        if (Junk(a) != Junk(b)) return Junk(a) ? b : a;
        return a.Length <= b.Length ? a : b;
    }

    /// <summary>Уносит файл из приёма, ничего не удаляя.</summary>
    private static void Quarantine(string full, string targetDir)
    {
        try
        {
            Directory.CreateDirectory(targetDir);
            System.IO.File.Move(full, Unique(Path.Combine(targetDir, Path.GetFileName(full))));
        }
        catch (IOException) { /* занят, заберём на следующем проходе */ }
        catch (UnauthorizedAccessException) { }
    }

    private static string Sha256(string path)
    {
        using var s = System.IO.File.OpenRead(path);
        using var h = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(h.ComputeHash(s));
    }

    /// <summary>Занят ли файл, например его прямо сейчас дописывает Corel.</summary>
    private static bool IsLocked(string path)
    {
        try
        {
            using var s = System.IO.File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>На всякий случай: если имя занято, добавляем счётчик.</summary>
    private static string Unique(string path)
    {
        if (!System.IO.File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!System.IO.File.Exists(p)) return p;
        }
    }

    /// <summary>После разбора приём должен пустеть, вычищаем оставшиеся пустые папки.</summary>
    private static void CleanEmptyDirs(string root)
    {
        foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(p => p.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
