using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Tanba.Storage;

/// <summary>Соединения с базой и разворачивание схемы при первом запуске.</summary>
public sealed class Db
{
    private readonly string _connString;

    public Db(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        using var pragma = c.CreateCommand();
        // WAL нужен, чтобы сканер писал, пока интерфейс читает.
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return c;
    }

    /// <summary>Разворачивает schema.sql, если таблиц ещё нет. Возвращает true, если это был первый запуск.</summary>
    public bool EnsureSchema()
    {
        using var c = Open();

        using (var check = c.CreateCommand())
        {
            check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='files'";
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return false;
        }

        // PRAGMA journal_mode нельзя выполнять внутри транзакции, поэтому вырезаем,
        // эти же прагмы уже выставлены в Open().
        var sql = ReadEmbedded("Tanba.schema.sql");
        var body = string.Join('\n', sql
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase)));

        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = body;
        cmd.ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    /// <summary>
    /// Догоняет схему на уже существующей базе. Идемпотентно, безопасно звать каждый запуск.
    /// Полноценных миграций пока нет, только то, что нужно сейчас.
    /// </summary>
    public void Migrate()
    {
        using var c = Open();

        if (!HasColumn(c, "files", "file_ctime"))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "ALTER TABLE files ADD COLUMN file_ctime INTEGER;"
                                + "CREATE INDEX IF NOT EXISTS ix_files_ctime ON files(file_ctime);"
                                + "CREATE INDEX IF NOT EXISTS ix_files_mtime ON files(file_mtime);";
                cmd.ExecuteNonQuery();
            }

        // Каталоги живут в той же таблице, что и файлы: каталог показывается
        // в сетке, ищется тегами и попадает в отбор наравне с ними.
        if (!HasColumn(c, "files", "kind"))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = """
                    ALTER TABLE files ADD COLUMN kind TEXT NOT NULL DEFAULT 'file';
                    ALTER TABLE files ADD COLUMN cover_file_id INTEGER REFERENCES files(id) ON DELETE SET NULL;
                    CREATE INDEX IF NOT EXISTS ix_files_kind ON files(kind);
                    """;
                cmd.ExecuteNonQuery();
            }

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS catalog_items (
                  catalog_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                  item_id    INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                  sort_order INTEGER NOT NULL DEFAULT 0,
                  added_at   INTEGER NOT NULL,
                  PRIMARY KEY (catalog_id, item_id)
                ) WITHOUT ROWID;

                CREATE INDEX IF NOT EXISTS ix_ci_item ON catalog_items(item_id);

                CREATE TRIGGER IF NOT EXISTS catalog_single_parent
                BEFORE INSERT ON catalog_items
                WHEN (SELECT kind FROM files WHERE id = NEW.item_id) = 'catalog'
                     AND EXISTS (SELECT 1 FROM catalog_items WHERE item_id = NEW.item_id)
                BEGIN
                  SELECT RAISE(ABORT, 'каталог уже лежит в другом каталоге');
                END;

                CREATE TRIGGER IF NOT EXISTS catalog_not_self
                BEFORE INSERT ON catalog_items
                WHEN NEW.catalog_id = NEW.item_id
                BEGIN
                  SELECT RAISE(ABORT, 'каталог не может лежать сам в себе');
                END;

                DROP VIEW IF EXISTS v_dupes;
                CREATE VIEW v_dupes AS
                SELECT sha256, COUNT(*) AS copies, SUM(size) - MIN(size) AS wasted
                FROM files
                WHERE sha256 IS NOT NULL AND kind = 'file'
                GROUP BY sha256 HAVING COUNT(*) > 1;
                """;
            cmd.ExecuteNonQuery();
        }

        // Формат стал таким же условием отбора, как тег, значит его надо
        // сохранять вместе с отбором, иначе закладка тихо потеряет половину.
        if (!HasColumn(c, "saved_queries", "ext_list"))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "ALTER TABLE saved_queries ADD COLUMN ext_list TEXT;";
                cmd.ExecuteNonQuery();
            }

        // Год больше не группа тегов: он отбирается диапазоном дат.
        // Сносим только если им никто не успел воспользоваться.
        using var drop = c.CreateCommand();
        drop.CommandText = """
            DELETE FROM tag_groups
            WHERE name = 'Год'
              AND NOT EXISTS (
                SELECT 1 FROM file_tags ft
                JOIN tags t ON t.id = ft.tag_id
                WHERE t.group_id = tag_groups.id);
            """;
        drop.ExecuteNonQuery();
    }

    private static bool HasColumn(SqliteConnection c, string table, string column)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM pragma_table_info('{table}') WHERE name = $c";
        cmd.Parameters.AddWithValue("$c", column);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static string ReadEmbedded(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Ресурс {name} не вшит в сборку. Есть: {string.Join(", ", asm.GetManifestResourceNames())}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}

/// <summary>Мелкие удобства поверх ADO.NET, чтобы не разводить церемонии.</summary>
public static class SqliteExtensions
{
    public static SqliteCommand Sql(this SqliteConnection c, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }

    public static long ScalarLong(this SqliteCommand cmd)
    {
        var o = cmd.ExecuteScalar();
        return o is null or DBNull ? 0 : Convert.ToInt64(o);
    }

    public static string? Str(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static long? Num(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
}
