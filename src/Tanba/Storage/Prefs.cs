using Microsoft.Data.Sqlite;

namespace Tanba.Storage;

/// <summary>
/// Пары ключ-значение из таблицы settings. Здесь лежит то, что человек
/// поменял руками: откуда брать обновления и когда их искали в прошлый раз.
/// Автозапуск сюда не пишем: его настоящее состояние знает Windows,
/// а вторая копия правды рано или поздно разойдётся с первой.
/// </summary>
public sealed class Prefs(Repo repo)
{
    // Адрес репозитория и ключ доступа сюда не попадают: они вшиты в сборку,
    // см. Tanba.csproj. Так человеку нечего вписывать, а ключ не лежит на диске.
    public const string UpdateSeen = "update.checked_at";

    /// Язык интерфейса, ru или en. Пусто значит «как в Windows», см. Lang.
    public const string Lang = "ui.lang";

    public string? Get(string key)
    {
        using var c = repo.Open();
        return Get(c, key);
    }

    public static string? Get(SqliteConnection c, string key)
    {
        using var cmd = c.Sql("SELECT v FROM settings WHERE k = $k", ("$k", key));
        var o = cmd.ExecuteScalar();
        return o is null or DBNull ? null : Convert.ToString(o);
    }

    public string Get(string key, string fallback)
    {
        var v = Get(key);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    public bool Flag(string key, bool fallback = false) =>
        Get(key) switch { null or "" => fallback, "1" or "true" => true, _ => false };

    public void Set(string key, string? value)
    {
        using var c = repo.Open();
        if (string.IsNullOrEmpty(value))
        {
            using var del = c.Sql("DELETE FROM settings WHERE k = $k", ("$k", key));
            del.ExecuteNonQuery();
            return;
        }
        using var cmd = c.Sql(
            "INSERT INTO settings (k, v) VALUES ($k, $v) ON CONFLICT(k) DO UPDATE SET v = excluded.v",
            ("$k", key), ("$v", value));
        cmd.ExecuteNonQuery();
    }

    public void Set(string key, bool value) => Set(key, value ? "1" : "0");
}
