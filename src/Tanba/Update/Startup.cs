using Microsoft.Win32;
using Velopack.Locators;

namespace Tanba.Update;

/// <summary>
/// Запуск вместе с Windows.
///
/// Правда о том, включён он или нет, живёт в реестре, а не у нас в базе:
/// человек может выключить Tanba в «Параметры, Приложения, Автозагрузка»,
/// и программа обязана показывать то же самое, что показывает система.
/// </summary>
public static class Startup
{
    /// <summary>Имя записи. Оно же видно в списке автозагрузки Windows.</summary>
    private const string Name = "Tanba";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// Windows держит здесь свою пометку, включена запись или выключена.
    /// Ключ недокументированный, но именно его правит переключатель в параметрах,
    /// и без него программа врала бы про своё состояние.
    private const string ApprovedKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>
    /// Путь, который переживёт обновление.
    ///
    /// В корне установки лежит переходник: маленький exe, запускающий текущую
    /// версию из папки current. Прописываем именно его, а не саму программу
    /// внутри current, по одной причине: при установке обновления содержимое
    /// current подменяется целиком, и вход в систему может попасть ровно в этот
    /// момент. Переходник лежит снаружи подмены и перекладывается заново после
    /// каждого обновления, то есть чинит себя сам.
    /// </summary>
    public static string TargetExe()
    {
        try
        {
            // До VelopackApp.Run() это свойство бросает исключение, а в неустановленной
            // копии осмысленных путей и нет. Оба случая ловим ниже.
            var loc = VelopackLocator.Current;
            var name = Path.GetFileName(loc.ThisExeRelativePath);

            if (!string.IsNullOrEmpty(loc.RootAppDir) && !string.IsNullOrEmpty(name))
            {
                var stub = Path.Combine(loc.RootAppDir, name);
                if (File.Exists(stub)) return stub;

                // Переходника нет: старая или переносная раскладка. Берём саму программу.
                if (!string.IsNullOrEmpty(loc.AppContentDir))
                {
                    var inside = Path.Combine(loc.AppContentDir, loc.ThisExeRelativePath!);
                    if (File.Exists(inside)) return inside;
                }
            }
        }
        catch (Exception) { /* не установлено, значит обычный exe */ }

        return Environment.ProcessPath ?? "";
    }

    /// <summary>Строка команды целиком. В трей, а не окном: см. Program.cs.</summary>
    private static string Command() => $"\"{TargetExe()}\" {Args.Tray}";

    public static bool IsOn()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(Name) is null) return false;

            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // Первый байт нечётный значит «выключено пользователем»: 3 и 11.
            // Чётные 2 и 6 значат «включено».
            if (approved?.GetValue(Name) is byte[] { Length: > 0 } mark && (mark[0] & 1) != 0)
                return false;

            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Возвращает получившееся состояние, а не то, о котором попросили.</summary>
    public static bool Set(bool on)
    {
        try
        {
            if (on)
            {
                using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                run?.SetValue(Name, Command(), RegistryValueKind.String);

                // Пометку Windows о выключении убираем совсем: своей записи
                // система тогда снова верит. Дописывать туда «включено» руками
                // нельзя, формат недокументирован и меняется.
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                if (approved?.GetValue(Name) is not null) approved.DeleteValue(Name, throwOnMissingValue: false);
            }
            else
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                run?.DeleteValue(Name, throwOnMissingValue: false);
            }
        }
        catch (Exception) { /* состояние вернём чтением, врать не будем */ }

        return IsOn();
    }

    /// <summary>
    /// Убирает за собой при удалении программы.
    ///
    /// Запись автозапуска пишем мы сами, и Velopack про неё не знает: он умеет
    /// снимать ярлыки, а ключ Run в его исходниках не упоминается вовсе.
    /// Без этого после удаления в «Автозагрузке» навсегда остаётся строка
    /// «Tanba», ведущая на стёртый файл. Заодно снимаем и пометку Windows
    /// о выключении, иначе она переживает саму программу.
    /// </summary>
    public static void Forget()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(Name, throwOnMissingValue: false);

            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch (Exception) { /* удаление уже идёт, мешать ему нечем */ }
    }

    /// <summary>
    /// Поправляет записанный путь, если он разъехался с настоящим.
    /// Зовётся после обновления: на случай, если раскладка установки изменится.
    /// </summary>
    public static void Refresh()
    {
        if (!IsOn()) return;
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(Name) as string == Command()) return;
            run?.SetValue(Name, Command(), RegistryValueKind.String);
        }
        catch (Exception) { }
    }
}

/// <summary>Ключи командной строки. Собраны в одном месте, чтобы не разъезжались.</summary>
public static class Args
{
    /// Подняться в трей молча: так программа стартует вместе с Windows.
    public const string Tray = "--tray";

    /// Только сервер, без своего окна. Для отладки в браузере.
    public const string NoWindow = "--no-window";
}
