using System.Text.Json.Nodes;
using Passenger.App.State;
using Passenger.Sql;

namespace Passenger.App.Sql;

/// <summary>A server kept to connect to again: what <see cref="SqlConnectionInfo"/> holds, and so never a password.</summary>
public sealed record SqlServerEntry
{
    public string Name { get; init; } = string.Empty;

    public string Server { get; init; } = string.Empty;

    public SqlSignIn SignIn { get; init; }

    public string User { get; init; } = string.Empty;

    public bool TrustCertificate { get; init; }

    public SqlConnectionInfo Info => new(Name ?? string.Empty, Server, SignIn, User ?? string.Empty, TrustCertificate);

    public static SqlServerEntry Of(SqlConnectionInfo info) =>
        new() { Name = info.Name, Server = info.Server, SignIn = info.SignIn, User = info.User, TrustCertificate = info.TrustCertificate };
}

/// <summary>The SQL Servers the person connected to, in the store's sqlServers.json, every folder alike, in the order
/// they were added: the SQL Server window lists them, each to connect to again with its password asked.</summary>
public sealed record SqlServersState : IStateDocument<SqlServersState>
{
    public IReadOnlyList<SqlServerEntry> Servers { get; init; } = [];

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    public SqlServersState Clamped() => this with { Servers = [.. (Servers ?? []).Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Server))] };

    /// <summary>With a server kept, in the place of the one of its title kept before.</summary>
    public SqlServersState With(SqlConnectionInfo info) =>
        this with { Servers = [.. Servers.Where(s => s.Info.Title != info.Title), SqlServerEntry.Of(info)] };

    public SqlServersState Without(SqlConnectionInfo info) => this with { Servers = [.. Servers.Where(s => s.Info.Title != info.Title)] };
}
