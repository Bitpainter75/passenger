namespace Passenger.Sql;

/// <summary>How a connection signs in to a server.</summary>
public enum SqlSignIn
{
    /// <summary>As the person running Passenger (Windows, or Kerberos where the machine has a ticket).</summary>
    Integrated,

    /// <summary>A SQL Server login: a user name and a password.</summary>
    Login,
}

/// <summary>A server the person connects to: the name they gave it, its address as SQL Server's tools take one
/// ("host", "host,1433", "host\instance"), how they sign in, and whether its certificate is taken unchecked, as a
/// development server's self-signed one must be. Its password is no part of it: asked when connecting, kept nowhere.</summary>
public sealed record SqlConnectionInfo(string Name, string Server, SqlSignIn SignIn, string User, bool TrustCertificate)
{
    /// <summary>What the window calls it: its name, else its server.</summary>
    public string Title => Name.Length > 0 ? Name : Server;
}

public enum SqlObjectKind
{
    Table,
    View,
}

/// <summary>A table or view of a database, by its schema and name.</summary>
public sealed record SqlObject(SqlObjectKind Kind, string Schema, string Name)
{
    /// <summary>Its name as a script writes it: both parts in brackets.</summary>
    public string Quoted => SqlScripts.Name(Schema, Name);

    /// <summary>Its name as a person reads it, the default schema left unsaid.</summary>
    public string Shown => Schema == "dbo" ? Name : $"{Schema}.{Name}";
}

/// <summary>A table's column: its name, its type as a script writes it ("nvarchar(50)", "decimal(18, 2)"), whether it
/// takes NULL, numbers itself and is of the primary key, and its default's expression. From a server it carries the
/// name of its default's constraint, which a script must name to drop it.</summary>
public sealed record SqlColumn(string Name, string Type, bool Nullable = true, bool Identity = false, bool PrimaryKey = false, string? Default = null)
{
    public string? DefaultConstraint { get; init; }

    /// <summary>Whether the server computes it or keeps it itself (a computed column, a rowversion), so no row writes it.</summary>
    public bool ReadOnly { get; init; }
}

/// <summary>A table as its designer shows it: where it is, and its columns in their order. From a server it carries the
/// name of its primary key's constraint.</summary>
public sealed record SqlTableDesign(string Schema, string Name, IReadOnlyList<SqlColumn> Columns)
{
    public string? KeyConstraint { get; init; }

    public string Quoted => SqlScripts.Name(Schema, Name);

    public IEnumerable<SqlColumn> Key => Columns.Where(c => c.PrimaryKey);
}

/// <summary>A column in a designer: what it is now, and the name it had in the table as read, null for a new one.</summary>
public sealed record SqlColumnEdit(string? Was, SqlColumn Now);

/// <summary>A result's column: its name and its type as the server names it.</summary>
public sealed record SqlResultColumn(string Name, string Type);

/// <summary>The rows one statement returned, each a value a column (null for NULL), and whether there were more than
/// were read.</summary>
public sealed record SqlRows(IReadOnlyList<SqlResultColumn> Columns, IReadOnlyList<object?[]> Rows, bool Cut);

/// <summary>What the server said beside the rows: a statement's count of rows, a PRINT, or an error with its line in the
/// script.</summary>
public sealed record SqlMessage(string Text, bool IsError, int? Line = null);

/// <summary>A script run: each statement's rows in order, the messages, how long it took, and whether an error ended it.</summary>
public sealed record SqlRun(IReadOnlyList<SqlRows> Results, IReadOnlyList<SqlMessage> Messages, TimeSpan Took, bool Failed);
