using Passenger.Sql;

namespace Passenger.App.Sql;

public enum SqlNodeKind
{
    Server,
    Database,
    Folder,
    Object,
    Column,
}

/// <summary>A row of the SQL Server window's tree: a server, a database of it, the tables or the views of that, one of
/// them, or a column. What is under a row is read from the server as the row is first opened; a server's row holds the
/// connection while it is connected, which every row under it works through.</summary>
public sealed class SqlNode(SqlNodeKind kind, string title, int depth, SqlNode? holder) : Observable, ITreeRow
{
    private bool isExpanded;
    private string detail = string.Empty;

    public SqlNodeKind Kind { get; } = kind;

    public string Title { get; } = title;

    public int Depth { get; } = depth;

    /// <summary>The row this one is under; null for a server's.</summary>
    public SqlNode? Holder { get; } = holder;

    /// <summary>The server as kept, on a server's row.</summary>
    public SqlConnectionInfo? Info { get; init; }

    /// <summary>The connection, on a server's row while it is connected.</summary>
    public SqlServer? Connected { get; set; }

    /// <summary>The table or view, on its row.</summary>
    public SqlObject? Object { get; init; }

    /// <summary>The rows under it, null until read.</summary>
    public IReadOnlyList<SqlNode>? Children { get; set; }

    /// <summary>What is said after its title, muted: a column's type, a server's state.</summary>
    public string Detail
    {
        get => detail;
        set => Set(ref detail, value);
    }

    public bool Expands => Kind != SqlNodeKind.Column;

    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }

    /// <summary>The server's row it is under, or is.</summary>
    public SqlNode ServerRow => Kind == SqlNodeKind.Server ? this : Holder!.ServerRow;

    /// <summary>The database it is in, null on a server's row.</summary>
    public string? Database => Kind switch
    {
        SqlNodeKind.Server => null,
        SqlNodeKind.Database => Title,
        _ => Holder!.Database,
    };

    /// <summary>The table or view it is, or is a column of.</summary>
    public SqlObject? Of => Object ?? (Kind == SqlNodeKind.Column ? Holder!.Object : null);
}

/// <summary>Where a script runs: a connected server's row and the database, the login's own where none is named.</summary>
public sealed record SqlTarget(SqlNode Server, string? Database)
{
    public string Shown => Database is null ? Server.Title : $"{Server.Title} › {Database}";
}
