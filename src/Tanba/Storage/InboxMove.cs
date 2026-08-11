namespace Tanba.Storage;

/// <summary>
/// Переезд приёма под новое имя, база.
///
/// Папку двигает Config.RenameOldInbox, и подвинуть её целиком удаётся не
/// всегда: открытый в Corel файл остаётся лежать в старой. Поэтому путь в
/// базе ставится не по намерению, а по факту, отдельно для каждой строки.
/// Строка обязана указывать туда, где файл лежит прямо сейчас, иначе он
/// пропадает с экрана разбора, оставаясь на диске.
///
/// Отсюда же следует, что звать это можно сколько угодно раз: следующий
/// запуск донесёт то, что не поддалось сегодня, и поправит его строку.
/// </summary>
public static class InboxMove
{
    public static void Carry(Config cfg, Repo repo)
    {
        var oldDir = Path.Combine(cfg.Root, Config.OldInboxName);
        var oldPfx = Config.OldInboxName + '\\';
        var newPfx = Config.InboxName + '\\';

        using var c = repo.Open();

        // Обычный день: старой папки нет и строк с её именем нет. Уходим
        // раньше, чем что-нибудь прочитаем, чтобы не платить за это каждым
        // запуском до конца времён.
        if (!Directory.Exists(oldDir))
        {
            using var any = c.Sql("SELECT EXISTS(SELECT 1 FROM files WHERE rel_path LIKE $p)",
                ("$p", oldPfx + "%"));
            if (any.ScalarLong() == 0) return;
        }

        var rows = new List<(long Id, string Rel)>();
        using (var cmd = c.Sql("""
            SELECT id, rel_path FROM files
            WHERE rel_path LIKE $a OR rel_path LIKE $b
            """, ("$a", oldPfx + "%"), ("$b", newPfx + "%")))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1)));

        var fixedUp = 0;
        foreach (var (id, rel) in rows)
        {
            var name = Path.GetFileName(rel);
            var inNew = File.Exists(Path.Combine(cfg.Inbox, name));
            var inOld = File.Exists(Path.Combine(oldDir, name));

            // Ни там, ни там: файл унесли мимо программы. Это не наше дело,
            // пусть с ним разбирается обход хранилища, у него для этого есть
            // номер файла на томе.
            if (!inNew && !inOld) continue;

            var want = (inNew ? newPfx : oldPfx) + name;
            if (want == rel) continue;

            repo.SetRelPath(c, id, want);
            fixedUp++;
        }

        if (fixedUp > 0) Console.WriteLine($"Приём переименован, путей поправлено: {fixedUp}");
    }
}
