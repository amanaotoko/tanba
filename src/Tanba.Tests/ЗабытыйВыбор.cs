using Tanba;

namespace Tanba.Tests;

/// <summary>
/// Мастер первого запуска спрашивал, где держать архив, всё готовил и не
/// запоминал ответ: RootStore.Write не вызывался ниоткуда. Программа при
/// каждом запуске открывала мастер заново, хотя была установлена и настроена.
///
/// Ошибка прожила до человека и мешала ему каждый день, поэтому здесь она
/// проверяется с двух сторон: и сам склад, и то, что выбор доезжает до
/// настройки, по которой программа ищет хранилище.
/// </summary>
public sealed class ЗабытыйВыбор : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("tanba-home-").FullName;
    private readonly string? _wasEnv = Environment.GetEnvironmentVariable("TANBA_ROOT");

    public ЗабытыйВыбор()
    {
        RootStore.HomeForTests = _home;
        // Переменная окружения старше файла и победила бы его: на время
        // проверки убираем, иначе она же и ответит вместо склада.
        Environment.SetEnvironmentVariable("TANBA_ROOT", null);
    }

    [Fact]
    public void Выбранное_место_переживает_перезапуск()
    {
        Assert.Null(RootStore.Read());
        Assert.Null(Config.Load());

        var chosen = Directory.CreateTempSubdirectory("tanba-store-").FullName;
        RootStore.Write(chosen);

        Assert.Equal(chosen, RootStore.Read());

        // То, ради чего всё это: следующий запуск не спрашивает заново.
        var cfg = Config.Load();
        Assert.NotNull(cfg);
        Assert.Equal(Path.GetFullPath(chosen), cfg!.Root);

        Directory.Delete(chosen, recursive: true);
    }

    /// <summary>Та самая связка: путь от ответа человека до следующего запуска.</summary>
    [Fact]
    public void Мастер_запоминает_выбор_а_не_только_принимает_его()
    {
        var chosen = Directory.CreateTempSubdirectory("tanba-store-").FullName;

        var cfg = Setup.Accept(chosen);
        Assert.Equal(Path.GetFullPath(chosen), cfg.Root);

        // Программа перезапустилась: спрашивать больше нечего.
        var again = Config.Load();
        Assert.NotNull(again);
        Assert.Equal(cfg.Root, again!.Root);

        Directory.Delete(chosen, recursive: true);
    }

    [Fact]
    public void Неудачная_запись_не_роняет_запуск()
    {
        // Папку подменяем файлом: создать в ней настройку невозможно.
        var blocked = Path.Combine(_home, "занято");
        File.WriteAllText(blocked, "не папка");
        RootStore.HomeForTests = blocked;

        RootStore.Write(@"C:\где-то");     // не должно бросить
        Assert.Null(RootStore.Read());

        RootStore.HomeForTests = _home;
    }

    public void Dispose()
    {
        RootStore.HomeForTests = null;
        Environment.SetEnvironmentVariable("TANBA_ROOT", _wasEnv);
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }
}
