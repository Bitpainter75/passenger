using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace Passenger.Sql;

/// <summary>What a server refused or could not be reached for, in its own words.</summary>
public sealed class SqlFailure(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A SQL Server the person is connected to: what it holds, scripts run on it and rows changed, each call on a
/// connection of the client's pool to the database it names, so nothing is held between calls and a server that went
/// away fails the next call alone. Its password lives here for as long as the connection is kept, and nowhere else.
/// What goes wrong is told as <see cref="SqlFailure"/>, the client's own types staying in this project. Any thread.</summary>
public sealed class SqlServer(SqlConnectionInfo info, string? password)
{
    /// <summary>How a result's column with no name is headed, as SQL Server's tools head one.</summary>
    public const string Unnamed = "(No column name)";

    /// <summary>On Windows the client's managed networking, as on every other system: its native one is a library under
    /// Microsoft's own terms, which Passenger does not carry. Before the client's first use.</summary>
    static SqlServer() => AppContext.SetSwitch("Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows", true);

    public SqlConnectionInfo Info { get; } = info;

    private string Connection(string? database)
    {
        SqlConnectionStringBuilder builder = new()
        {
            DataSource = Info.Server,
            ApplicationName = "Passenger",
            ConnectTimeout = 15,
            TrustServerCertificate = Info.TrustCertificate,
        };
        if (database is not null)
        {
            builder.InitialCatalog = database;
        }
        if (Info.SignIn == SqlSignIn.Integrated)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = Info.User;
            builder.Password = password ?? string.Empty;
        }
        return builder.ConnectionString;
    }

    private static bool IsTheServers(Exception error) =>
        error is DbException or InvalidOperationException or ArgumentException or PlatformNotSupportedException or IOException or NotSupportedException;

    private async Task<T> AskAsync<T>(string? database, Func<SqlConnection, Task<T>> ask, CancellationToken cancel)
    {
        try
        {
            await using SqlConnection connection = new(Connection(database));
            await connection.OpenAsync(cancel).ConfigureAwait(false);
            return await ask(connection).ConfigureAwait(false);
        }
        catch (Exception error) when (IsTheServers(error) && !cancel.IsCancellationRequested)
        {
            throw new SqlFailure(error.Message, error);
        }
    }

    private static async Task<List<T>> ReadAsync<T>(SqlConnection connection, string query, Func<DbDataReader, T> row, CancellationToken cancel, params SqlParameter[] given)
    {
        await using SqlCommand command = new(query, connection);
        command.Parameters.AddRange(given);
        List<T> rows = [];
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancel).ConfigureAwait(false);
        while (await reader.ReadAsync(cancel).ConfigureAwait(false))
        {
            rows.Add(row(reader));
        }
        return rows;
    }

    /// <summary>Connects, telling the server's version ("16.00.4165"). Throws <see cref="SqlFailure"/> where it cannot.</summary>
    public Task<string> ConnectAsync(CancellationToken cancel = default) => AskAsync(null, connection => Task.FromResult(connection.ServerVersion), cancel);

    /// <summary>The databases the login may open, by name.</summary>
    public async Task<IReadOnlyList<string>> DatabasesAsync(CancellationToken cancel = default) => await AskAsync(null, connection =>
        ReadAsync(connection, "SELECT name FROM sys.databases WHERE state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name", r => r.GetString(0), cancel), cancel).ConfigureAwait(false);

    /// <summary>A database's own tables and views, by schema and name.</summary>
    public async Task<IReadOnlyList<SqlObject>> ObjectsAsync(string database, CancellationToken cancel = default) => await AskAsync(database, connection =>
        ReadAsync(connection,
            "SELECT s.name, o.name, o.type FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0 ORDER BY s.name, o.name",
            r => new SqlObject(r.GetString(2).Trim() == "V" ? SqlObjectKind.View : SqlObjectKind.Table, r.GetString(0), r.GetString(1)), cancel), cancel).ConfigureAwait(false);

    /// <summary>A table's or view's columns as its designer takes them, a table's with its key and their constraints' names.</summary>
    public Task<SqlTableDesign> DesignAsync(string database, SqlObject of, CancellationToken cancel = default) => AskAsync(database, async connection =>
    {
        const string Query = """
            SELECT c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed, d.definition, d.name,
                   CASE WHEN ic.column_id IS NULL THEN 0 ELSE 1 END, k.name
            FROM sys.columns c
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            LEFT JOIN sys.default_constraints d ON d.parent_object_id = c.object_id AND d.parent_column_id = c.column_id
            LEFT JOIN sys.key_constraints k ON k.parent_object_id = c.object_id AND k.type = 'PK'
            LEFT JOIN sys.index_columns ic ON ic.object_id = c.object_id AND ic.index_id = k.unique_index_id AND ic.column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(@name)
            ORDER BY c.column_id
            """;
        string? key = null;
        List<SqlColumn> columns = await ReadAsync(connection, Query, r =>
        {
            key ??= r.IsDBNull(11) ? null : r.GetString(11);
            string type = r.GetString(1);
            return new SqlColumn(r.GetString(0), SqlTypes.Written(type, r.GetInt16(2), r.GetByte(3), r.GetByte(4)), r.GetBoolean(5), r.GetBoolean(6), r.GetInt32(10) == 1,
                r.IsDBNull(8) ? null : SqlScripts.Bare(r.GetString(8)))
            {
                DefaultConstraint = r.IsDBNull(9) ? null : r.GetString(9),
                ReadOnly = r.GetBoolean(7) || SqlTypes.ServerWritten(type),
            };
        }, cancel, new SqlParameter("@name", of.Quoted)).ConfigureAwait(false);
        return new SqlTableDesign(of.Schema, of.Name, columns) { KeyConstraint = key };
    }, cancel);

    /// <summary>A view's script as the server keeps it, or null where it shows none (an encrypted view).</summary>
    public Task<string?> DefinitionAsync(string database, SqlObject view, CancellationToken cancel = default) => AskAsync(database, async connection =>
        (await ReadAsync(connection, "SELECT OBJECT_DEFINITION(OBJECT_ID(@name))", r => r.IsDBNull(0) ? null : r.GetString(0), cancel, new SqlParameter("@name", view.Quoted))
            .ConfigureAwait(false)).FirstOrDefault(), cancel);

    /// <summary>Runs a script, a batch at a time (<see cref="SqlScripts.Batches"/>), reading at most <paramref name="most"/>
    /// rows of each statement's; a batch that fails says why with its line in the script, and the batches after it still
    /// run, as SQL Server's tools run them. Cancelled, it ends with what it had read. It throws only where the server
    /// cannot be reached at all.</summary>
    public async Task<SqlRun> RunAsync(string? database, string script, int most, CancellationToken cancel = default)
    {
        Stopwatch took = Stopwatch.StartNew();
        List<SqlRows> results = [];
        List<SqlMessage> messages = [];
        bool failed = false;
        try
        {
            await using SqlConnection connection = new(Connection(database));
            await connection.OpenAsync(cancel).ConfigureAwait(false);
            connection.InfoMessage += (_, said) =>
            {
                lock (messages)
                {
                    messages.Add(new SqlMessage(said.Message, IsError: false));
                }
            };
            foreach (SqlBatch batch in SqlScripts.Batches(script))
            {
                try
                {
                    await RunAsync(connection, batch, most, results, messages, cancel).ConfigureAwait(false);
                }
                catch (SqlException error) when (!cancel.IsCancellationRequested)
                {
                    failed = true;
                    lock (messages)
                    {
                        messages.AddRange(error.Errors.Cast<SqlError>().Select(e => new SqlMessage($"Msg {e.Number}, Level {e.Class}, State {e.State}: {e.Message}", IsError: true, e.LineNumber > 0 ? e.LineNumber + batch.Line - 1 : null)));
                    }
                    // A connection the error broke runs nothing more.
                    if (connection.State != ConnectionState.Open)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception error) when (IsTheServers(error) && !cancel.IsCancellationRequested)
        {
            throw new SqlFailure(error.Message, error);
        }
        catch (Exception stopped) when (cancel.IsCancellationRequested && (stopped is OperationCanceledException || IsTheServers(stopped)))
        {
            failed = true;
            messages.Add(new SqlMessage("Stopped", IsError: true));
        }
        lock (messages)
        {
            return new SqlRun(results, [.. messages], took.Elapsed, failed);
        }
    }

    private static async Task RunAsync(SqlConnection connection, SqlBatch batch, int most, List<SqlRows> results, List<SqlMessage> messages, CancellationToken cancel)
    {
        await using SqlCommand command = new(batch.Text, connection) { CommandTimeout = 0 };
        command.StatementCompleted += (_, done) =>
        {
            lock (messages)
            {
                messages.Add(new SqlMessage($"({done.RecordCount:N0} row{(done.RecordCount == 1 ? string.Empty : "s")} affected)", IsError: false));
            }
        };
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancel).ConfigureAwait(false);
        do
        {
            if (reader.FieldCount == 0)
            {
                continue;
            }
            List<SqlResultColumn> columns = [.. Enumerable.Range(0, reader.FieldCount).Select(i => new SqlResultColumn(reader.GetName(i) is { Length: > 0 } name ? name : Unnamed, reader.GetDataTypeName(i)))];
            List<object?[]> rows = [];
            bool cut = false;
            while (await reader.ReadAsync(cancel).ConfigureAwait(false))
            {
                if (rows.Count == most)
                {
                    cut = true;
                    break;
                }
                object?[] row = new object?[columns.Count];
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] = Value(reader, i);
                }
                rows.Add(row);
            }
            results.Add(new SqlRows(columns, rows, cut));
            if (cut)
            {
                // The rest of a result cut short is not sent for: the statement is told to stop.
                command.Cancel();
                break;
            }
        }
        while (await reader.NextResultAsync(cancel).ConfigureAwait(false));
    }

    /// <summary>A cell's value, null for NULL; a type .NET has no value for (a spatial one, a hierarchyid) as its type's name.</summary>
    private static object? Value(SqlDataReader reader, int column)
    {
        try
        {
            return reader.IsDBNull(column) ? null : reader.GetValue(column);
        }
        catch (Exception unread) when (unread is InvalidCastException or FileNotFoundException or NotSupportedException or InvalidOperationException)
        {
            return $"<{reader.GetDataTypeName(column)}>";
        }
    }

    private async Task<string> SettingAsync(string property, CancellationToken cancel) => (await AskAsync(null, connection =>
        ReadAsync(connection, "SELECT CAST(SERVERPROPERTY(@property) AS nvarchar(4000))", r => r.IsDBNull(0) ? string.Empty : r.GetString(0), cancel, new SqlParameter("@property", property)),
        cancel).ConfigureAwait(false)).FirstOrDefault() ?? string.Empty;

    /// <summary>The folder on the server's own disk where it keeps backups, ending in its separator; empty where it says none.</summary>
    public async Task<string> BackupFolderAsync(CancellationToken cancel = default) =>
        SqlScripts.In(await SettingAsync("InstanceDefaultBackupPath", cancel).ConfigureAwait(false));

    /// <summary>Backs a database up to a file on the server's own disk (<see cref="SqlScripts.Backup"/>), its progress the
    /// run's messages.</summary>
    public Task<SqlRun> BackupAsync(string database, string file, CancellationToken cancel = default) => RunAsync(null, SqlScripts.Backup(database, file), 1, cancel);

    /// <summary>Restores a database from a backup on the server's own disk (<see cref="SqlScripts.Restore"/>), over the one
    /// of its name: the backup's files are read first and each given a place in the server's folders. The client's
    /// pooled connections are let go first, as one to the database would hold it.</summary>
    public async Task<SqlRun> RestoreAsync(string database, string file, CancellationToken cancel = default)
    {
        List<(string Logical, bool IsLog)> files = await AskAsync(null, connection =>
            ReadAsync(connection, $"RESTORE FILELISTONLY FROM DISK = {SqlScripts.Literal(file)}", r => (r.GetString(0), r.GetString(2).Trim() == "L"), cancel), cancel).ConfigureAwait(false);
        string data = await SettingAsync("InstanceDefaultDataPath", cancel).ConfigureAwait(false);
        string log = await SettingAsync("InstanceDefaultLogPath", cancel).ConfigureAwait(false);
        SqlConnection.ClearAllPools();
        return await RunAsync(null, SqlScripts.Restore(database, file, files, data, log.Length > 0 ? log : data), 1, cancel).ConfigureAwait(false);
    }

    /// <summary>Writes a database, its schema and its rows, to a .bacpac on this machine, each step told as it is taken.</summary>
    public Task<SqlRun> ExportAsync(string database, string file, CancellationToken cancel = default) =>
        PackageAsync(database, services => services.ExportBacpac(file, database, cancellationToken: cancel), cancel);

    /// <summary>Makes a new database of a .bacpac on this machine, each step told as it is taken.</summary>
    public Task<SqlRun> ImportAsync(string database, string file, CancellationToken cancel = default) => PackageAsync(null, services =>
    {
        using BacPackage package = BacPackage.Load(file);
        services.ImportBacpac(package, database, cancel);
    }, cancel);

    /// <summary>A step of Microsoft's own framework for packages (DacFx), which works on the calling thread: run on the thread
    /// pool by whoever calls, its messages the run's, a failure the run's error in the framework's words.</summary>
    private Task<SqlRun> PackageAsync(string? database, Action<DacServices> work, CancellationToken cancel)
    {
        Stopwatch took = Stopwatch.StartNew();
        List<SqlMessage> messages = [];
        bool failed = false;
        // The framework speaks the machine's language unless told: English, as the server's own messages are.
        System.Globalization.CultureInfo spoken = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        try
        {
            // The framework sends its maker usage data unless this is in the environment.
            Passenger.Core.OwnEnvironment.Set("DACFX_TELEMETRY_OPTOUT", "1");
            DacServices services = new(Connection(database));
            services.Message += (_, said) =>
            {
                lock (messages)
                {
                    messages.Add(new SqlMessage(said.Message.Message, said.Message.MessageType == DacMessageType.Error));
                }
            };
            work(services);
        }
        catch (Exception error) when (error is DacServicesException or OperationCanceledException or IOException or UnauthorizedAccessException || IsTheServers(error))
        {
            failed = true;
            lock (messages)
            {
                // What the framework told as it failed is not said twice.
                for (Exception? said = cancel.IsCancellationRequested ? new OperationCanceledException("Stopped") : error; said is not null; said = said.InnerException)
                {
                    if (!messages.Any(m => m.IsError && said.Message.Contains(m.Text, StringComparison.Ordinal)))
                    {
                        messages.Add(new SqlMessage(said.Message.Trim(), IsError: true));
                    }
                }
            }
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = spoken;
        }
        lock (messages)
        {
            return Task.FromResult(new SqlRun([], [.. messages], took.Elapsed, failed));
        }
    }

    /// <summary>Runs statements as one change: all of them, or on a failure none. With <paramref name="rows"/>, the change
    /// stands only if exactly that many rows changed in all, so a row's edit never reaches another row.</summary>
    public Task ChangeAsync(string database, IReadOnlyList<string> statements, IReadOnlyList<KeyValuePair<string, object>>? values = null, int? rows = null,
        CancellationToken cancel = default) => AskAsync(database, async connection =>
    {
        await using SqlTransaction change = (SqlTransaction)await connection.BeginTransactionAsync(cancel).ConfigureAwait(false);
        int changed = 0;
        foreach (string statement in statements)
        {
            await using SqlCommand command = new(statement, connection, change) { CommandTimeout = 0 };
            foreach ((string name, object value) in values ?? [])
            {
                command.Parameters.AddWithValue(name, value);
            }
            changed += Math.Max(0, await command.ExecuteNonQueryAsync(cancel).ConfigureAwait(false));
        }
        if (rows is { } wanted && changed != wanted)
        {
            await change.RollbackAsync(cancel).ConfigureAwait(false);
            throw new SqlFailure(changed == 0 ? "The row is no longer there as it was read: read the table again" : $"{changed:N0} rows would have changed, so nothing was");
        }
        await change.CommitAsync(cancel).ConfigureAwait(false);
        return true;
    }, cancel);
}
