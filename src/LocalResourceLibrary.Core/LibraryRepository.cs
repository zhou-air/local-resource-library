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
        // BEGIN IMMEDIATE serializes schema detection and migration across independent processes.
        using var transaction = connection.BeginTransaction();
        var version = Convert.ToInt32(Scalar(connection, transaction, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version > 4) throw new NotSupportedException("数据库来自较新版本，请使用较新版本的应用打开。");
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
                """);
        }
        if (version < 3)
        {
            Execute(connection, transaction, "ALTER TABLE Item ADD COLUMN favicon BLOB; PRAGMA user_version = 3;");
        }
        if (version < 4)
        {
            Execute(connection, transaction, """
                ALTER TABLE Project ADD COLUMN is_pinned INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE Project ADD COLUMN sort_order INTEGER NOT NULL DEFAULT 0;
                UPDATE Project SET sort_order = (SELECT COUNT(*) FROM Project AS other
                    WHERE other.name COLLATE NOCASE < Project.name COLLATE NOCASE);
                PRAGMA user_version = 4;
                """);
        }
        transaction.Commit();
        // A single writer and concurrent readers can share this local database without a server.
        Execute(connection, null, "PRAGMA journal_mode=WAL;");
    }

    public SqliteConnection Connect()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            Execute(connection, null, "PRAGMA busy_timeout=10000;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
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
        using (var command = Command(connection, transaction, "SELECT id,name,description,is_pinned,sort_order FROM Project ORDER BY is_pinned DESC,sort_order,name COLLATE NOCASE,id;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) projects.Add(new Project(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0, reader.GetInt64(4)));
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
                   "SELECT id,type,target,alias,description,note,created_at,updated_at,last_opened_at,open_count,volume_id,file_id,favicon FROM Item ORDER BY created_at DESC;"))
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
                    reader.IsDBNull(10) || reader.IsDBNull(11) ? null : new FileIdentity(reader.GetString(10), reader.GetString(11)),
                    reader.IsDBNull(12) ? null : (byte[])reader.GetValue(12)));
            }
        }
        return new LibrarySnapshot(items, projects);
    }

    public static ResourceItem? ReadItem(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        var projects = new List<Project>();
        using (var command = Command(connection, transaction, """
                   SELECT p.id,p.name,p.description,p.is_pinned,p.sort_order
                   FROM Project AS p INNER JOIN ProjectItem AS membership ON membership.project_id=p.id
                   WHERE membership.item_id=$id ORDER BY p.name COLLATE NOCASE,p.id;
                   """, ("$id", id)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) projects.Add(new Project(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0, reader.GetInt64(4)));
        using var itemCommand = Command(connection, transaction, """
            SELECT id,type,target,alias,description,note,created_at,updated_at,last_opened_at,open_count,volume_id,file_id,favicon
            FROM Item WHERE id=$id;
            """, ("$id", id));
        using var itemReader = itemCommand.ExecuteReader();
        if (!itemReader.Read()) return null;
        return new ResourceItem(itemReader.GetString(0), itemReader.GetString(1), itemReader.GetString(2), itemReader.GetString(3),
            itemReader.GetString(4), itemReader.GetString(5), ReadTime(itemReader.GetString(6)), ReadTime(itemReader.GetString(7)),
            itemReader.IsDBNull(8) ? null : ReadTime(itemReader.GetString(8)), itemReader.GetInt64(9), false, projects.ToArray(),
            itemReader.IsDBNull(10) || itemReader.IsDBNull(11) ? null : new FileIdentity(itemReader.GetString(10), itemReader.GetString(11)),
            itemReader.IsDBNull(12) ? null : (byte[])itemReader.GetValue(12));
    }

    public static Project? ReadProject(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using var command = Command(connection, transaction,
            "SELECT id,name,description,is_pinned,sort_order FROM Project WHERE id=$id;", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new Project(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt64(3) != 0, reader.GetInt64(4)) : null;
    }

    public static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ReadTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
