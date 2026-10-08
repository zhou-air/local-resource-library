using System.Globalization;
using Microsoft.Data.Sqlite;

namespace LocalResourceLibrary.Core;

internal sealed class LibraryRepository
{
    private readonly string connectionString;
    public string DatabasePath { get; }

    public LibraryRepository(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            ForeignKeys = true,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 10
        }.ToString();
        using var connection = Connect();
        using var versionCommand = Command(connection, null, "PRAGMA user_version;");
        var version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version > 2) throw new NotSupportedException("数据库来自较新版本，请使用较新版本的应用打开。");
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS Item (
                id TEXT PRIMARY KEY,
                type TEXT NOT NULL,
                target TEXT NOT NULL,
                path_key TEXT NOT NULL UNIQUE,
                alias TEXT NOT NULL DEFAULT '',
                description TEXT NOT NULL DEFAULT '',
                note TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_opened_at TEXT,
                open_count INTEGER NOT NULL DEFAULT 0 CHECK (open_count >= 0)
            );
            CREATE TABLE IF NOT EXISTS Project (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                name_key TEXT NOT NULL UNIQUE,
                description TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS ProjectItem (
                project_id TEXT NOT NULL REFERENCES Project(id) ON DELETE CASCADE,
                item_id TEXT NOT NULL REFERENCES Item(id) ON DELETE CASCADE,
                PRIMARY KEY (project_id, item_id)
            );
            CREATE INDEX IF NOT EXISTS ix_ProjectItem_item ON ProjectItem(item_id);
            """);
        if (version < 2)
        {
            Execute(connection, transaction, """
                ALTER TABLE Item ADD COLUMN volume_id TEXT;
                ALTER TABLE Item ADD COLUMN file_id TEXT;
                PRAGMA user_version = 2;
                """);
        }
        transaction.Commit();
    }

    public SqliteConnection Connect()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static int Execute(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteScalar();
    }

    public static LibrarySnapshot ReadSnapshot(SqliteConnection connection, SqliteTransaction transaction)
    {
        var projects = new List<Project>();
        using (var command = Command(connection, transaction, "SELECT id,name,description FROM Project ORDER BY name COLLATE NOCASE;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) projects.Add(new Project(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        var projectsById = projects.ToDictionary(project => project.Id);
        var memberships = new Dictionary<string, List<Project>>();
        using (var command = Command(connection, transaction, "SELECT item_id,project_id FROM ProjectItem;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var itemId = reader.GetString(0);
                if (!memberships.TryGetValue(itemId, out var list)) memberships[itemId] = list = [];
                list.Add(projectsById[reader.GetString(1)]);
            }
        }
        var items = new List<ResourceItem>();
        using (var command = Command(connection, transaction,
                   "SELECT id,type,target,alias,description,note,created_at,updated_at,last_opened_at,open_count,volume_id,file_id FROM Item ORDER BY created_at DESC;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var itemId = reader.GetString(0);
                var itemProjects = memberships.TryGetValue(itemId, out var list)
                    ? list.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase).ToArray() : [];
                items.Add(new ResourceItem(itemId, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), ReadTime(reader.GetString(6)), ReadTime(reader.GetString(7)),
                    reader.IsDBNull(8) ? null : ReadTime(reader.GetString(8)), reader.GetInt64(9), false, itemProjects,
                    reader.IsDBNull(10) || reader.IsDBNull(11) ? null : new FileIdentity(reader.GetString(10), reader.GetString(11))));
            }
        }
        return new LibrarySnapshot(items, projects);
    }

    public static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ReadTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
