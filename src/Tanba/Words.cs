namespace Tanba;

/// <summary>
/// Строки, которые показывает не окно, а сама программа: значок в трее,
/// его меню и всплывающие подсказки.
///
/// Их горстка, поэтому отдельного словаря здесь нет: две ветки на строку
/// читаются проще, чем таблица ключей ради одиннадцати значений. Всё
/// остальное, что видит человек, рисует интерфейс, и его словарь лежит
/// в ui/strings.js.
///
/// Язык берётся из настроек при запуске и меняется, когда его меняют на
/// экране настроек: значок в трее живёт дольше окна и обязан говорить на
/// том же языке, что и оно.
/// </summary>
public static class Words
{
    private static string _lang = Lang.Ru;

    /// <summary>Кому пересобрать свои надписи. На это подписан значок в трее.</summary>
    public static event Action? Changed;

    public static void Use(string? lang)
    {
        var next = Lang.Pick(lang);
        Lang.SetCurrent(next);
        if (next == _lang) return;
        _lang = next;
        Changed?.Invoke();
    }

    private static bool En => _lang == Lang.En;

    public static string Open => En ? "Open Tanba" : "Открыть Tanba";
    public static string Rescan => En ? "Rescan the Inbox" : "Пересканировать приём";
    public static string Quit => En ? "Quit" : "Выход";

    public static string AllSorted => En ? "Tanba, all sorted" : "Tanba, всё разобрано";

    /// <summary>Подсказка значка: сколько файлов ждёт разбора.</summary>
    public static string Waiting(int n) => En
        ? $"Tanba, {n} {(n == 1 ? "file is" : "files are")} waiting for triage"
        : $"Tanba, {n} {Plural(n, "файл ждёт", "файла ждут", "файлов ждут")} разбора";

    /// <summary>Уведомление о накопившемся. Имя папки не переводится, оно на диске.</summary>
    public static string PileUp(int n) => En
        ? $"{n} in «{Config.InboxName}» now. Sort them in one go."
        : $"В «{Config.InboxName}» накопилось {n}. Разберёшь одной пачкой.";

    private static string Plural(int n, string one, string few, string many)
    {
        var a = Math.Abs(n) % 100;
        var b = a % 10;
        if (a is > 10 and < 20) return many;
        return b switch { 1 => one, > 1 and < 5 => few, _ => many };
    }
}
