using System.Data;
using System.Reflection;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace FocusPlus.Data;

public interface IConnectionFactory
{
    string DatabasePath { get; }
    SqliteConnection Open();
}

/// <summary>Owns the SQLite file: WAL mode, migrations, integrity and information helpers.</summary>
public sealed class Database : IConnectionFactory, IDisposable
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _connectionString;

    public Database(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("A database path is required.", nameof(databasePath));
        }

        DatabasePath = Path.GetFullPath(databasePath);
        BackupDirectory = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "Backups");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
    }

    public string DatabasePath { get; }

    public string BackupDirectory { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Creates the file if needed, applies pending migrations and seeds the OPGI baseline.</summary>
    public void Initialize()
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;";
            pragma.ExecuteNonQuery();
        }

        ApplyMigrations(connection);
        SeedData.EnsureBaseline(connection);
    }

    private static void ApplyMigrations(SqliteConnection connection)
    {
        var scripts = LoadMigrations();
        var current = connection.ExecuteScalar<long>("PRAGMA user_version;");
        if (current >= scripts.Count)
        {
            return;
        }

        for (var index = (int)current; index < scripts.Count; index++)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = scripts[index];
            command.ExecuteNonQuery();

            using var versionCommand = connection.CreateCommand();
            versionCommand.Transaction = transaction;
            versionCommand.CommandText = $"PRAGMA user_version = {index + 1};";
            versionCommand.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private static List<string> LoadMigrations()
    {
        var assembly = typeof(Database).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var scripts = new List<string>(names.Count);
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            scripts.Add(reader.ReadToEnd());
        }

        return scripts;
    }

    /// <summary>True when the database file answers and passes a quick integrity check.</summary>
    public bool IsHealthy(out string message)
    {
        try
        {
            using var connection = Open();
            var result = connection.ExecuteScalar<string>("PRAGMA quick_check;");
            var healthy = string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
            message = healthy ? "SQLite OK" : result ?? "unknown";
            return healthy;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    public DatabaseInfo GetInfo()
    {
        using var connection = Open();
        var file = new FileInfo(DatabasePath);
        var walMode = connection.ExecuteScalar<string>("PRAGMA journal_mode;") ?? "unknown";

        int Scalar(string table)
        {
            try
            {
                return connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table};");
            }
            catch (SqliteException)
            {
                return 0;
            }
        }

        return new DatabaseInfo
        {
            Path = DatabasePath,
            SizeBytes = file.Exists ? file.Length : 0,
            JournalMode = walMode,
            SchemaVersion = (int)connection.ExecuteScalar<long>("PRAGMA user_version;"),
            Employees = Scalar("Employees"),
            Devices = Scalar("Devices"),
            Punches = Scalar("Punches"),
            Departments = Scalar("Departments"),
            WorkRules = Scalar("WorkRules"),
            LastBackupPath = GetLastBackupPath()
        };
    }

    public string? GetLastBackupPath()
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return null;
        }

        return new DirectoryInfo(BackupDirectory)
            .GetFiles("*.db")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
    }
}

public sealed record DatabaseInfo
{
    public required string Path { get; init; }
    public long SizeBytes { get; init; }
    public required string JournalMode { get; init; }
    public int SchemaVersion { get; init; }
    public int Employees { get; init; }
    public int Devices { get; init; }
    public int Punches { get; init; }
    public int Departments { get; init; }
    public int WorkRules { get; init; }
    public string? LastBackupPath { get; init; }

    public double SizeMb => Math.Round(SizeBytes / 1024d / 1024d, 2);
    public bool IsWal => string.Equals(JournalMode, "wal", StringComparison.OrdinalIgnoreCase);
}
