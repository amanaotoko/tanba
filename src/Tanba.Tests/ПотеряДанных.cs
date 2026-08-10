using Tanba.Storage;

namespace Tanba.Tests;

/// <summary>
/// Четыре случая, в которых программа теряла или уносила чужие файлы.
/// Все четыре нашла ревизия кода, все четыре здесь воспроизводятся.
/// Тесты написаны после починки, поэтому их ценность не в том, что они
/// зелёные сегодня, а в том, что они покраснеют, если починку отменят.
/// </summary>
public sealed class ПотеряДанных
{
    /// <summary>
    /// Файл, открытый в другой программе, терял строку вместе со всеми тегами.
    /// Сканер не мог его прочитать, пропускал, а «не увидел» шло тем же путём,
    /// что и «файла нет»: строка удалялась, теги уходили каскадом.
    /// Разметил сорок файлов, один открыт для просмотра, нажал «Разложить».
    /// </summary>
    [Fact]
    public void Занятый_файл_сохраняет_свои_теги()
    {
        using var b = new Bench();
        var path = b.Drop("проба.cdr");
        b.Ingest.ScanInbox();

        long id;
        using (var c = b.Repo.Open())
        {
            id = b.Repo.ListInbox(c)[0].Id;
            b.Repo.ApplyTag(c, [id], b.SomeTag(), true);
            Assert.Single(b.Repo.TagsOf(c, id));
        }

        // Держим файл так же, как его держит Corel: никому не давая читать.
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            b.Ingest.ScanInbox();

        using (var c = b.Repo.Open())
        {
            var still = b.Repo.ListInbox(c).SingleOrDefault(f => f.Id == id);
            Assert.NotNull(still);
            Assert.Single(b.Repo.TagsOf(c, id));
        }
    }

    /// <summary>
    /// Слияние дублей стирало последнюю копию файла. Выживший выбирался
    /// по колонке is_missing, то есть по мнению базы, а мнение устаревает:
    /// файл могли удалить в проводнике. Программа радостно сообщала
    /// «дубликатов слито: 1», удалив единственный оставшийся файл.
    /// </summary>
    [Fact]
    public void Не_сливает_с_тем_чего_нет_на_диске()
    {
        using var b = new Bench();

        b.Drop("первый.txt", "одинаковые байты");
        b.FileEverything();
        var filed = Assert.Single(b.Filed());

        // Архивную копию убрали мимо программы. База об этом ещё не знает.
        File.Delete(filed);

        b.Drop("второй.txt", "одинаковые байты");
        b.Ingest.ScanInbox();
        long second;
        using (var c = b.Repo.Open())
        {
            second = b.Repo.ListInbox(c)[0].Id;
            b.Repo.ApplyTag(c, [second], b.SomeTag(), true);
        }

        var res = b.Ingest.File([second]);

        Assert.Equal(0, res.MergedDuplicates);
        Assert.Equal(1, res.Moved);
        Assert.Single(b.Filed());
    }

    /// <summary>
    /// Потерянная база уносила весь архив в приём. Пустая таблица означала
    /// «все файлы чужие», и обход перекладывал их в «СОХРАНИ СЮДА», срезая
    /// номера и ломая раскладку по годам. Без вопроса, в фоне, при запуске.
    /// </summary>
    [Fact]
    public void Пустая_база_не_разбирает_архив()
    {
        using var b = new Bench();
        for (var i = 1; i <= 3; i++) { b.Drop($"файл{i}.txt", $"байты {i}"); b.FileEverything(); }
        Assert.Equal(3, b.Filed().Length);

        // База потерялась: файл удалили, испортили, или хранилище перенесли
        // на другую машину без него.
        using (var c = b.Repo.Open())
        using (var wipe = c.Sql("DELETE FROM files"))
            wipe.ExecuteNonQuery();

        var r = b.Store.Run();

        Assert.Equal(0, r.Adopted);
        Assert.Equal(3, r.Refused);
        Assert.Equal(3, b.Filed().Length);
        Assert.Empty(Directory.EnumerateFiles(b.Cfg.Inbox));
    }

    /// <summary>
    /// Обход хранилища падал целиком, если на диске попадалась папка,
    /// в которую нет доступа: «System Volume Information» есть на любом томе.
    /// Перечисление ленивое, и исключение случалось уже за пределами catch.
    /// Слежка за файлами при этом выключалась молча и навсегда.
    /// </summary>
    [Fact]
    public void Чужая_закрытая_папка_не_ломает_обход()
    {
        using var b = new Bench();
        b.Drop("живой.txt");
        b.FileEverything();

        var locked = Path.Combine(b.Cfg.Root, "закрытая");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "внутри.txt"), "не читать");
        if (!DenyRead(locked)) return;   // права не дали закрыть папку, проверять нечего

        try
        {
            // Уводим файл из хранилища: только так обход доходит до полного
            // прохода по диску, где и лежит закрытая папка.
            var filed = b.Filed()[0];
            var away = Path.Combine(b.Cfg.Root, "уехал.txt");
            File.Move(filed, away);

            var r = b.Store.Run();

            Assert.Equal(1, r.MovedOut);
            Assert.Equal(0, r.Lost);
        }
        finally { AllowRead(locked); }
    }

    // ── Закрываем папку от чтения, как это делает Windows со своими ──────

    private static bool DenyRead(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.ListDirectory,
                System.Security.AccessControl.AccessControlType.Deny));
            info.SetAccessControl(acl);
            return true;
        }
        catch (Exception) { return false; }
    }

    private static void AllowRead(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            var acl = info.GetAccessControl();
            acl.RemoveAccessRuleAll(new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.ListDirectory,
                System.Security.AccessControl.AccessControlType.Deny));
            info.SetAccessControl(acl);
        }
        catch (Exception) { }
    }
}
