using System.Globalization;
using System.Text;

namespace Passenger.Sql;

/// <summary>A batch of a script: its text, and the line of the script its first line is, counting from 1.</summary>
public readonly record struct SqlBatch(string Text, int Line);

/// <summary>Why a table's design cannot be scripted: said to the person in the designer, nothing run.</summary>
public sealed class SqlDesignException(string message) : Exception(message);

/// <summary>The T-SQL Passenger writes, as text alone: a table made, changed and dropped from its design, a view's
/// scripts, the rows a table opens with, a row's change by its key, and a script cut into its batches. Every name goes
/// in brackets, a "]" in it doubled, and every value a person typed travels as a parameter, never in the text, but a
/// backup's file, which BACKUP and RESTORE take as a literal alone.</summary>
public static class SqlScripts
{
    /// <summary>A name in brackets.</summary>
    public static string Name(string name) => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    /// <summary>A schema's object, both names in brackets.</summary>
    public static string Name(string schema, string name) => $"{Name(schema)}.{Name(name)}";

    /// <summary>A text as an N'…' literal, for the system procedures that take a name as text.</summary>
    public static string Literal(string text) => $"N'{text.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>The first rows of a table or view.</summary>
    public static string SelectTop(SqlObject from, int rows) =>
        $"SELECT TOP ({rows.ToString(CultureInfo.InvariantCulture)}) * FROM {from.Quoted}";

    public static string Drop(SqlObject dropped) => $"DROP {(dropped.Kind == SqlObjectKind.View ? "VIEW" : "TABLE")} {dropped.Quoted}";

    /// <summary>A new view's script for the person to finish.</summary>
    public static string NewView(string schema) => $"CREATE VIEW {Name(schema, "NewView")}\nAS\nSELECT 1 AS [Value]\n";

    /// <summary>A view's own script, as the server keeps it, turned to one that changes it: its first CREATE to ALTER.</summary>
    public static string AlterView(string definition)
    {
        int create = definition.IndexOf("CREATE", StringComparison.OrdinalIgnoreCase);
        return create < 0 ? definition : string.Concat(definition.AsSpan(0, create), "ALTER", definition.AsSpan(create + "CREATE".Length));
    }

    /// <summary>A database backed up whole to a file on the server's own disk, written over what is there, as a copy that
    /// leaves the server's chain of backups as it was.</summary>
    public static string Backup(string database, string file) =>
        $"BACKUP DATABASE {Name(database)} TO DISK = {Literal(file)} WITH INIT, COPY_ONLY, STATS = 10";

    /// <summary>A database restored from a backup on the server's disk, over the one of its name where there is one,
    /// whose connections are ended first: each of the backup's files (its logical name, and whether it is the log)
    /// moved to a file named for the database in the server's own folders, so a backup of another name or server
    /// restores. A last batch lets everyone in again whatever became of the restore.</summary>
    public static string Restore(string database, string file, IReadOnlyList<(string Logical, bool IsLog)> files, string dataFolder, string logFolder)
    {
        string named = Literal(database);
        StringBuilder script = new();
        script.Append(CultureInfo.InvariantCulture, $"IF DB_ID({named}) IS NOT NULL ALTER DATABASE {Name(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE\nGO\n");
        script.Append(CultureInfo.InvariantCulture, $"RESTORE DATABASE {Name(database)} FROM DISK = {Literal(file)} WITH REPLACE, STATS = 10");
        int data = 0;
        int logs = 0;
        foreach ((string logical, bool isLog) in files)
        {
            int at = isLog ? logs++ : data++;
            string count = at == 0 ? string.Empty : $"_{at.ToString(CultureInfo.InvariantCulture)}";
            string to = isLog ? $"{In(logFolder)}{database}_log{count}.ldf" : $"{In(dataFolder)}{database}{count}.{(at == 0 ? "mdf" : "ndf")}";
            script.Append(CultureInfo.InvariantCulture, $", MOVE {Literal(logical)} TO {Literal(to)}");
        }
        script.Append(CultureInfo.InvariantCulture, $"\nGO\nIF DB_ID({named}) IS NOT NULL ALTER DATABASE {Name(database)} SET MULTI_USER\n");
        return script.ToString();
    }

    /// <summary>A folder ending in its separator, the server's own kind: a Linux server's "/", a Windows one's "\".</summary>
    public static string In(string folder) =>
        folder.Length == 0 || folder[^1] is '/' or '\\' ? folder : folder + (folder.Contains('/') ? '/' : '\\');

    private static void Check(SqlTableDesign design)
    {
        if (design.Name.Trim().Length == 0 || design.Schema.Trim().Length == 0)
        {
            throw new SqlDesignException("The table needs a schema and a name");
        }
        if (design.Columns.Count == 0)
        {
            throw new SqlDesignException("A table needs at least one column");
        }
        foreach (SqlColumn column in design.Columns)
        {
            if (column.Name.Trim().Length == 0)
            {
                throw new SqlDesignException("Every column needs a name");
            }
            if (column.Type.Trim().Length == 0)
            {
                throw new SqlDesignException($"{column.Name} needs a type");
            }
            if (column.PrimaryKey && column.Nullable)
            {
                throw new SqlDesignException($"{column.Name} is of the primary key, whose columns take no NULL");
            }
        }
        if (design.Columns.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            throw new SqlDesignException($"Two columns are named {twice.Key}");
        }
        if (design.Columns.Count(c => c.Identity) > 1)
        {
            throw new SqlDesignException("Only one column of a table can be its identity");
        }
    }

    /// <summary>A column as CREATE TABLE and ADD write it.</summary>
    private static string Column(SqlColumn column)
    {
        StringBuilder written = new($"{Name(column.Name)} {column.Type.Trim()}");
        if (column.Identity)
        {
            written.Append(" IDENTITY(1, 1)");
        }
        written.Append(column.Nullable ? " NULL" : " NOT NULL");
        if (column.Default is { Length: > 0 } value)
        {
            written.Append(CultureInfo.InvariantCulture, $" DEFAULT ({Bare(value)})");
        }
        return written.ToString();
    }

    /// <summary>A default's expression without the brackets the server keeps it in, however many.</summary>
    public static string Bare(string expression)
    {
        string bare = expression.Trim();
        while (bare.Length >= 2 && bare[0] == '(' && Closes(bare))
        {
            bare = bare[1..^1].Trim();
        }
        return bare;
    }

    /// <summary>Whether a text's first bracket closes at its last character.</summary>
    private static bool Closes(string text)
    {
        int depth = 0;
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'')
            {
                quoted = !quoted;
            }
            else if (!quoted && c == '(')
            {
                depth++;
            }
            else if (!quoted && c == ')' && --depth == 0)
            {
                return i == text.Length - 1;
            }
        }
        return false;
    }

    private static string KeyName(SqlTableDesign design) => Name($"PK_{design.Name}");

    private static string Key(SqlTableDesign design) =>
        $"CONSTRAINT {KeyName(design)} PRIMARY KEY ({string.Join(", ", design.Key.Select(c => Name(c.Name)))})";

    /// <summary>The script that makes a table of a design. Throws <see cref="SqlDesignException"/> for one it cannot make.</summary>
    public static string CreateTable(SqlTableDesign design)
    {
        Check(design);
        List<string> lines = [.. design.Columns.Select(Column)];
        if (design.Key.Any())
        {
            lines.Add(Key(design));
        }
        return $"CREATE TABLE {design.Quoted}\n(\n    {string.Join(",\n    ", lines)}\n)";
    }

    /// <summary>The statements that turn a table as read into its design as changed, in the order they must run: the
    /// key let go of where it changes, columns dropped, renamed, retyped, their defaults moved, new ones added, the key
    /// made again, the table renamed. None for a design left as it was. Throws <see cref="SqlDesignException"/> for a
    /// change SQL Server has no statement for.</summary>
    public static IReadOnlyList<string> AlterTable(SqlTableDesign before, string name, IReadOnlyList<SqlColumnEdit> edits)
    {
        SqlTableDesign after = new(before.Schema, name.Trim(), [.. edits.Select(e => e.Now)]);
        Check(after);
        string table = before.Quoted;
        Dictionary<string, SqlColumn> had = before.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        List<(SqlColumn Was, SqlColumn Now)> kept = [.. edits.Where(e => e.Was is not null && had.ContainsKey(e.Was)).Select(e => (had[e.Was!], e.Now))];
        List<SqlColumn> dropped = [.. before.Columns.Where(c => !kept.Any(k => k.Was == c))];
        if (kept.FirstOrDefault(k => k.Was.Identity != k.Now.Identity) is { Now: not null } identity)
        {
            throw new SqlDesignException($"{identity.Now.Name}: SQL Server cannot make an existing column an identity, or stop it being one");
        }
        List<string> script = [];
        bool retyped(SqlColumn was, SqlColumn now) => !Same(was.Type, now.Type) || was.Nullable != now.Nullable;
        // The key is rebuilt where its columns change, are renamed or retyped: no column of a key can be altered.
        bool rekeyed = !before.Key.Select(c => c.Name).SequenceEqual(kept.Where(k => k.Now.PrimaryKey).Select(k => k.Was.Name), StringComparer.OrdinalIgnoreCase)
            || edits.Any(e => e.Was is null && e.Now.PrimaryKey)
            || kept.Any(k => k.Was.PrimaryKey && (retyped(k.Was, k.Now) || k.Was.Name != k.Now.Name));
        if (rekeyed && before.Key.Any())
        {
            script.Add(before.KeyConstraint is { } constraint ? $"ALTER TABLE {table} DROP CONSTRAINT {Name(constraint)}"
                : throw new SqlDesignException("The table's primary key was read without its constraint's name, so it cannot be changed"));
        }
        foreach (SqlColumn gone in dropped)
        {
            DropDefault(gone);
            script.Add($"ALTER TABLE {table} DROP COLUMN {Name(gone.Name)}");
        }
        foreach ((SqlColumn was, SqlColumn now) in kept)
        {
            bool defaulted = Bare(was.Default ?? string.Empty) != Bare(now.Default ?? string.Empty);
            if (defaulted || retyped(was, now))
            {
                DropDefault(was);
            }
            if (was.Name != now.Name)
            {
                script.Add($"EXEC sp_rename {Literal($"{before.Schema}.{before.Name}.{was.Name}")}, {Literal(now.Name)}, N'COLUMN'");
            }
            if (retyped(was, now))
            {
                script.Add($"ALTER TABLE {table} ALTER COLUMN {Name(now.Name)} {now.Type.Trim()} {(now.Nullable ? "NULL" : "NOT NULL")}");
            }
            if ((defaulted || (retyped(was, now) && was.Default is { Length: > 0 })) && now.Default is { Length: > 0 } value)
            {
                script.Add($"ALTER TABLE {table} ADD DEFAULT ({Bare(value)}) FOR {Name(now.Name)}");
            }
        }
        foreach (SqlColumnEdit added in edits.Where(e => e.Was is null || !had.ContainsKey(e.Was)))
        {
            script.Add($"ALTER TABLE {table} ADD {Column(added.Now)}");
        }
        if (rekeyed && after.Key.Any())
        {
            script.Add($"ALTER TABLE {table} ADD {Key(before with { Columns = after.Columns })}");
        }
        if (after.Name != before.Name)
        {
            script.Add($"EXEC sp_rename {Literal($"{before.Schema}.{before.Name}")}, {Literal(after.Name)}");
        }
        return script;

        void DropDefault(SqlColumn column)
        {
            if (column.Default is { Length: > 0 })
            {
                script.Add(column.DefaultConstraint is { } constraint ? $"ALTER TABLE {table} DROP CONSTRAINT {Name(constraint)}"
                    : throw new SqlDesignException($"{column.Name}'s default was read without its constraint's name, so it cannot be changed"));
            }
        }
    }

    /// <summary>Whether two types are one, however they are spaced or cased ("nvarchar(50)" and "NVARCHAR (50)").</summary>
    private static bool Same(string type, string other) =>
        string.Equals(type.Replace(" ", string.Empty, StringComparison.Ordinal), other.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

    /// <summary>A row's change, the row told by its key's columns: <c>@v</c> the value written, <c>@k0</c>… the key's.</summary>
    public static string UpdateRow(SqlTableDesign table, string column) =>
        $"UPDATE {table.Quoted} SET {Name(column)} = @v WHERE {Where(table)}";

    /// <summary>A row's removal, told by its key: <c>@k0</c>… the key's values.</summary>
    public static string DeleteRow(SqlTableDesign table) => $"DELETE FROM {table.Quoted} WHERE {Where(table)}";

    /// <summary>A new row of the columns given, <c>@v0</c>… their values; of defaults alone where none is given.</summary>
    public static string InsertRow(SqlTableDesign table, IReadOnlyList<string> columns) => columns.Count == 0
        ? $"INSERT INTO {table.Quoted} DEFAULT VALUES"
        : $"INSERT INTO {table.Quoted} ({string.Join(", ", columns.Select(Name))}) VALUES ({string.Join(", ", columns.Select((_, i) => $"@v{i.ToString(CultureInfo.InvariantCulture)}"))})";

    private static string Where(SqlTableDesign table) =>
        string.Join(" AND ", table.Key.Select((c, i) => $"{Name(c.Name)} = @k{i.ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>A script's batches: cut at each line that says only GO, as SQL Server's tools cut one, a batch of no
    /// statement left out. A GO inside a comment or a string of several lines is a line of its batch.</summary>
    public static IReadOnlyList<SqlBatch> Batches(string script)
    {
        List<SqlBatch> batches = [];
        StringBuilder batch = new();
        int first = 1;
        int number = 0;
        bool comment = false;
        bool text = false;
        foreach (string line in script.Split('\n'))
        {
            number++;
            if (!comment && !text && line.Trim().TrimEnd(';').Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                End(number + 1);
                continue;
            }
            batch.Append(line).Append('\n');
            for (int i = 0; i < line.Length; i++)
            {
                if (comment)
                {
                    comment = !(line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/');
                    i += comment ? 0 : 1;
                }
                else if (text)
                {
                    text = line[i] != '\'';
                }
                else if (line[i] == '\'')
                {
                    text = true;
                }
                else if (line[i] == '-' && i + 1 < line.Length && line[i + 1] == '-')
                {
                    break;
                }
                else if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    comment = true;
                    i++;
                }
            }
        }
        End(number + 1);
        return batches;

        void End(int next)
        {
            if (batch.ToString().Trim().Length > 0)
            {
                batches.Add(new SqlBatch(batch.ToString().TrimEnd(), first));
            }
            batch.Clear();
            first = next;
        }
    }
}
