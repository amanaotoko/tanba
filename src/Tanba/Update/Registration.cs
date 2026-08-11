using Microsoft.Win32;

namespace Tanba.Update;

/// <summary>
/// Запись Tanba в списке установленных программ Windows.
///
/// Саму запись целиком пишет Velopack, и лезть в неё не нужно: при установке
/// и при каждом обновлении он переписывает все двенадцать своих значений
/// подряд, без чтения и без условий, так что любая ручная правка молча
/// откатится. Исправляем ровно одно, и только потому, что оно испорчено.
/// </summary>
public static class Registration
{
    private const string Key =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Tanba";

    /// <summary>
    /// Переписывает размер числом того типа, который Windows умеет читать.
    ///
    /// Velopack пишет EstimatedSize как QWORD: размер папки у него получается
    /// 64-битным, и таким же уходит в реестр. Windows же читает это значение
    /// строго как DWORD и на чужом типе получает ошибку вместо числа, поэтому
    /// в карточке программы размер выходит пустым. Из четырёхсот с лишним
    /// записей на машине неправильный тип был только у нашей.
    ///
    /// Само число верное, меняем только тип. Зовём при каждом обычном запуске:
    /// обновление вернёт QWORD обратно, и починить это один раз нельзя.
    /// </summary>
    public static void Fix()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true);
            if (key is null) return;  // не установлено, а запущено из папки сборки

            if (key.GetValueKind("EstimatedSize") != RegistryValueKind.QWord) return;

            var kb = Convert.ToInt64(key.GetValue("EstimatedSize"));
            if (kb <= 0 || kb > int.MaxValue) return;

            key.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
        }
        catch (Exception) { /* значения нет или ключ закрыт: не наша беда */ }
    }
}
