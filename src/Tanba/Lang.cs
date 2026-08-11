using System.Globalization;

namespace Tanba;

/// <summary>
/// Язык интерфейса. Их два, и выбор хранится в настройках, то есть в базе.
///
/// Мастер первого запуска показывается раньше, чем база существует, поэтому
/// там язык берётся из языка Windows: русская система открывает русский
/// мастер, любая другая английский. Выбранное там уходит в настройки и
/// дальше меняется на экране настроек.
///
/// Теги сюда не относятся: это данные человека, а не наш интерфейс, и
/// заведённые по-русски остаются русскими при любом переключении.
/// </summary>
public static class Lang
{
    public const string Ru = "ru";
    public const string En = "en";

    /// <summary>Язык Windows, сведённый к тем двум, что у нас есть.</summary>
    public static string OfWindows() =>
        CultureInfo.InstalledUICulture.TwoLetterISOLanguageName
            .Equals(Ru, StringComparison.OrdinalIgnoreCase) ? Ru : En;

    /// <summary>Сохранённый выбор, а если его нет или он бессмысленный, язык Windows.</summary>
    public static string Pick(string? stored) => stored is Ru or En ? stored : OfWindows();

    /// <summary>
    /// Язык, на котором программа говорит прямо сейчас. Ставится при запуске
    /// из настроек и меняется вместе с ними, см. Words.Use.
    ///
    /// Нужен там, где подпись рождается на сервере и уходит в окно готовой
    /// строкой: панель отбора в библиотеке присылает названия своих групп
    /// вместе с данными, и отличить их в окне от имени тега уже нельзя.
    /// </summary>
    public static string Current { get; private set; } = OfWindows();

    internal static void SetCurrent(string lang) => Current = Pick(lang);

    /// <summary>Выбор одной из двух подписей по текущему языку.</summary>
    public static string Say(string ru, string en) => Current == En ? en : ru;
}
