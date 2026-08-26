namespace Tanba.Host;

/// <summary>
/// Какую тему выбрал человек, для самого окна.
///
/// Страница знает тему из localStorage и сообщает её форме уже после своей
/// загрузки, а красить фон окну нужно раньше: при запуске и между
/// навигациями окно светлого человека иначе стоит тёмным. Поэтому выбор
/// лежит ещё и файлом рядом с root.txt, где его можно прочитать до того,
/// как загрузится хоть что-нибудь.
///
/// Расходиться с localStorage файл не может дольше одной загрузки страницы:
/// страница шлёт тему при каждом старте, и обработчик дописывает файл,
/// когда значение изменилось.
/// </summary>
public static class ThemeStore
{
    private static string PathTo => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Tanba", "theme.txt");

    public static bool Light()
    {
        try { return File.Exists(PathTo) && File.ReadAllText(PathTo).Trim() == "light"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static void Write(bool light)
    {
        try
        {
            if (Light() == light) return;   // страница шлёт тему на каждой загрузке
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathTo)!);
            File.WriteAllText(PathTo, light ? "light" : "dark");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Не смог запомнить тему: {e.Message}");
        }
    }
}
