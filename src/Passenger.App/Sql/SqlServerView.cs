using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Tables;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Sql;

namespace Passenger.App.Sql;

/// <summary>The SQL Server window: at its left the servers the person connected to, each with its databases, their
/// tables and views and those's columns, read as a row is opened; beside them a script to write, the database it runs
/// in, and under it what the last run returned, a tab a result and one for the server's messages. A table or view opens
/// with its first rows; a table's with a primary key are written where they are shown, a cell at a time, a row added in
/// the row below them and rows deleted after a question. The window's actions do the rest: a table designed, a view's
/// script written, either dropped.</summary>
public sealed class SqlServerView : DockPanel
{
    /// <summary>The most rows a table or view opens with.</summary>
    public const int OpenedRows = 1000;

    /// <summary>The most rows of a statement of a script that are read.</summary>
    public const int MostRows = 10_000;

    private const string MessagesTab = "Messages";

    private readonly Func<SqlServersState> kept;
    private readonly Action<Func<SqlServersState, SqlServersState>> keep;
    private readonly Action<string> request;
    private readonly Action<string> say;
    private readonly Window owner;
    private readonly TreeRows<SqlNode> rows = [];
    private readonly ListBox tree = new() { Name = "SqlTree", Classes = { StyleClasses.Tree } };
    private readonly EmptyState empty = new();
    private readonly ComboBox targets = new() { Name = "SqlDatabase", MinWidth = 180, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox script;
    private readonly TextBlock status = MutedText.Of(string.Empty);
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CellGrid grid = new();
    private readonly SelectableTextBlock messages = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 8) };
    private readonly ScrollViewer messagesPage;
    private readonly List<SqlNode> servers = [];
    private List<SqlRowsTable> results = [];
    private int shown;
    private CancellationTokenSource? running;

    /// <summary>What opened the rows shown, to read them again after a change: where, and the table or view.</summary>
    private (SqlTarget Target, SqlObject Opened)? opened;

    public SqlServerView(Func<SqlServersState> kept, Action<Func<SqlServersState, SqlServersState>> keep, Action<string> request, Action<string> say, Window owner)
    {
        this.owner = owner;
        this.kept = kept;
        this.keep = keep;
        this.request = request;
        this.say = say;
        Children.Add(ToolBars.Side(
            ToolButtons.Of(AppActions.SqlConnect, IconKeys.Add, "Connect to SQL Server…", request),
            ToolButtons.Of(AppActions.SqlDisconnect, IconKeys.Detach, "Disconnect", request),
            ToolButtons.Of(AppActions.SqlRefresh, IconKeys.Rerun, "Refresh", request),
            ToolBars.Gap(),
            ToolButtons.Of(AppActions.SqlOpenData, IconKeys.Table, "Open Rows", request),
            ToolButtons.Of(AppActions.SqlNewTable, IconKeys.Add, "New Table…", request),
            ToolButtons.Of(AppActions.SqlDesign, IconKeys.Edit, "Design Table or Edit View…", request),
            ToolButtons.Of(AppActions.SqlDrop, IconKeys.Clear, "Drop…", request)));

        tree.ItemsSource = rows;
        ReusedRows.Use(tree, Row);
        TreeKeys.Attach<SqlNode>(tree, node => IdeErrors.Observe(ToggleAsync(node)), node =>
        {
            if (node.Kind != SqlNodeKind.Object)
            {
                return false;
            }
            request(AppActions.SqlOpenData);
            return true;
        });
        tree.DoubleTapped += (_, _) =>
        {
            if (tree.SelectedItem is SqlNode { Kind: SqlNodeKind.Object })
            {
                request(AppActions.SqlOpenData);
            }
            else if (tree.SelectedItem is SqlNode node)
            {
                IdeErrors.Observe(ToggleAsync(node));
            }
        };
        tree.SelectionChanged += (_, _) => Aim();
        ListMenus.Attach(tree, () => Chosen is not { } node ? [] : node.Kind switch
        {
            SqlNodeKind.Server => [Item(AppActions.SqlConnect, "Connect…"), Item(AppActions.SqlDisconnect, "Disconnect"), Item(AppActions.SqlRefresh, "Refresh"), Item(AppActions.SqlRestore, "Restore Database…"), Item(AppActions.SqlImport, "Import BACPAC…"), Item(AppActions.SqlForget, "Forget Server")],
            SqlNodeKind.Database or SqlNodeKind.Folder =>
            [
                Item(AppActions.SqlNewTable, "New Table…"), Item(AppActions.SqlNewView, "New View"), Item(AppActions.SqlRefresh, "Refresh"),
                Item(AppActions.SqlBackup, "Back Up Database…"), Item(AppActions.SqlRestore, "Restore Database…"), Item(AppActions.SqlExport, "Export BACPAC…"),
            ],
            _ => [Item(AppActions.SqlOpenData, "Open Rows"), Item(AppActions.SqlDesign, node.Of?.Kind == SqlObjectKind.View ? "Edit View" : "Design Table…"), Item(AppActions.SqlDrop, "Drop…")],
        });

        ReusedRows.Use(targets, () =>
        {
            TextBlock name = new();
            return name.Showing<SqlTarget>(target => name.Text = target.Shown);
        });
        ToolTip.SetTip(targets, "The database the script runs in");
        status.Name = "SqlStatus";
        status.Margin = new Thickness(10, 0);
        status.FontSize = 12;
        SetDock(status, Dock.Right);
        ToolFlow bar = new() { Margin = new Thickness(6, 2) };
        bar.Children.AddRange(
        [
            ToolButtons.Of(AppActions.SqlExecute, IconKeys.Run, "Execute", request), ToolButtons.Of(AppActions.SqlStop, IconKeys.Stop, "Stop", request), targets,
        ]);
        script = new TextBox
        {
            Name = "SqlScript",
            AcceptsReturn = true,
            AcceptsTab = true,
            PlaceholderText = "Write T-SQL here: Ctrl+Enter runs it, or what of it is selected",
            MinHeight = 84,
            MaxHeight = 220,
            FontSize = 13,
            [DockProperty] = Dock.Top,
        }.InCodeFont();
        script.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Enter && e.KeyModifiers == KeyModifiers.Control)
            {
                e.Handled = true;
                request(AppActions.SqlExecute);
            }
        }, RoutingStrategies.Tunnel);
        messagesPage = new ScrollViewer { Content = messages.InCodeFont(), IsVisible = false };
        grid.CellWritten += (_, write) => IdeErrors.Observe(WriteAsync(write));
        grid.RowsDeleted += (_, write) => IdeErrors.Observe(DeleteAsync(write));
        DockPanel right = new()
        {
            Children =
            {
                ToolBars.Top(bar),
                new Border { [DockProperty] = Dock.Top, Padding = new Thickness(8, 6), Child = script },
                ToolBars.Top(new DockPanel
                {
                    Children = { status, new ScrollViewer { Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled } },
                }),
                new Panel { Children = { grid, messagesPage } },
            },
        };
        Children.Add(new PaneSplit(new Panel { Children = { tree, empty } }, right, "1*,1,3*"));
        empty.ActionRequested += (_, action) => request(action);
        foreach (SqlServerEntry entry in kept().Servers)
        {
            servers.Add(ServerRow(entry.Info));
        }
        rows.Show(servers);
        Show(null, null);
        Paint();
    }

    private MenuItem Item(string action, string title) => ListMenus.Item(title, null, () => request(action));

    /// <summary>The row chosen in the tree.</summary>
    public SqlNode? Chosen => tree.SelectedItem as SqlNode;

    /// <summary>The tree's rows as shown, each indented by its depth: its title and what is said after it.</summary>
    public IReadOnlyList<string> Tree => [.. rows.Select(r => $"{new string(' ', r.Depth * 2)}{r.Title}{(r.Detail.Length > 0 ? $" ({r.Detail})" : string.Empty)}")];

    /// <summary>The script written.</summary>
    public string Script
    {
        get => script.Text ?? string.Empty;
        set => script.Text = value;
    }

    /// <summary>The table of rows shown, null on the messages' tab or before any run.</summary>
    public SqlRowsTable? Result => messagesPage.IsVisible || shown >= results.Count ? null : results[shown];

    /// <summary>The grid the rows are shown and written in.</summary>
    public CellGrid Grid => grid;

    /// <summary>The server's messages of the last run.</summary>
    public string Messages => messages.Text ?? string.Empty;

    /// <summary>What the bar says of the last run.</summary>
    public string Status => status.Text ?? string.Empty;

    /// <summary>Whether a script runs.</summary>
    public bool Running => running is not null;

    /// <summary>Where a script would run, or null with no server connected.</summary>
    public SqlTarget? Target => targets.SelectedItem as SqlTarget;

    /// <summary>Chooses a row of the tree by its title, the first of it under the row chosen, else anywhere.</summary>
    public bool Choose(string title)
    {
        int from = tree.SelectedIndex;
        SqlNode? found = rows.Skip(from + 1).FirstOrDefault(r => r.Title == title) ?? rows.FirstOrDefault(r => r.Title == title);
        tree.SelectedItem = found;
        return found is not null;
    }

    private SqlNode ServerRow(SqlConnectionInfo info) => new(SqlNodeKind.Server, info.Title, 0, null) { Info = info, Detail = "not connected" };

    /// <summary>The window its dialogs are shown over.</summary>
    private Window? Owner => owner;

    /// <summary>Why one of the window's actions cannot run here, else null.</summary>
    public string? WhyNot(string action)
    {
        SqlNode? node = Chosen;
        bool connected = node?.ServerRow.Connected is not null;
        const string NoServer = "No server is chosen in the SQL Server window";
        const string NotConnected = "The server chosen in the SQL Server window is not connected";
        const string Busy = "The SQL Server window is at work: Stop ends it";
        return action switch
        {
            AppActions.SqlConnect => null,
            AppActions.SqlForget => node is null ? NoServer : null,
            AppActions.SqlDisconnect or AppActions.SqlRefresh => node is null ? NoServer : connected ? null : NotConnected,
            AppActions.SqlRestore or AppActions.SqlImport => Running ? Busy : node is null ? NoServer : connected ? null : NotConnected,
            AppActions.SqlBackup or AppActions.SqlExport => Running ? Busy : node?.Database is null ? "No database is chosen in the SQL Server window" : connected ? null : NotConnected,
            AppActions.SqlExecute => Running ? Busy : Target is null ? "No SQL Server is connected" : string.IsNullOrWhiteSpace(Script) ? "No script is written in the SQL Server window" : null,
            AppActions.SqlStop => Running ? null : "The SQL Server window is not at work",
            AppActions.SqlNewTable or AppActions.SqlNewView => node?.Database is null ? "No database is chosen in the SQL Server window" : connected ? null : NotConnected,
            AppActions.SqlOpenData or AppActions.SqlDesign or AppActions.SqlDrop => node?.Of is null ? "No table or view is chosen in the SQL Server window" : connected ? null : NotConnected,
            _ => null,
        };
    }

    /// <summary>Asks for a server and connects to it; <paramref name="before"/> is one kept, connected to again.</summary>
    public async Task ConnectAsync(SqlNode? before = null)
    {
        if (Owner is not { } owner)
        {
            return;
        }
        SqlServer? connected = null;
        IReadOnlyList<string> databases = [];
        SqlConnectDialog dialog = new(before?.Info, async (info, password) =>
        {
            SqlServer server = new(info, password);
            try
            {
                await BackgroundWork.Run(async () =>
                {
                    await server.ConnectAsync();
                    databases = await server.DatabasesAsync();
                });
            }
            catch (SqlFailure refused)
            {
                return refused.Message;
            }
            connected = server;
            return null;
        });
        ConnectAsked?.Invoke(this, dialog);
        if (!await dialog.ShowDialog<bool>(owner) || connected is not { } made)
        {
            return;
        }
        keep(state => state.With(made.Info));
        SqlNode row = ServerRow(made.Info);
        row.Connected = made;
        row.Detail = string.Empty;
        row.Children = [.. databases.Select(d => new SqlNode(SqlNodeKind.Database, d, 1, row))];
        row.IsExpanded = true;
        int at = servers.FindIndex(s => s.Title == row.Title || s == before);
        if (at >= 0)
        {
            servers[at] = row;
        }
        else
        {
            servers.Add(row);
        }
        ShowTree();
        tree.SelectedItem = row;
        Paint();
    }

    /// <summary>The connect dialog, as it is about to be shown: a test fills it.</summary>
    public event EventHandler<SqlConnectDialog>? ConnectAsked;

    /// <summary>The table designer, as it is about to be shown: a test designs in it.</summary>
    public event EventHandler<SqlTableDialog>? DesignAsked;

    private void ShowTree() => rows.Show([.. TreeRows<SqlNode>.Shown(servers, n => n.Children)]);

    /// <summary>Lets go of the chosen row's server: its row stays, to connect to again.</summary>
    public void Disconnect()
    {
        if (Chosen?.ServerRow is { } server)
        {
            server.Connected = null;
            server.Children = null;
            server.IsExpanded = false;
            server.Detail = "not connected";
            ShowTree();
            Paint();
        }
    }

    /// <summary>Forgets the chosen row's server: its row goes, and it is kept no longer.</summary>
    public void Forget()
    {
        if (Chosen?.ServerRow is { Info: { } info } server)
        {
            keep(state => state.Without(info));
            servers.Remove(server);
            ShowTree();
            Paint();
        }
    }

    /// <summary>Opens or closes a row, reading what is under it the first time: a server not connected asks to connect.</summary>
    public async Task ToggleAsync(SqlNode node)
    {
        if (node.IsExpanded)
        {
            node.IsExpanded = false;
            rows.Close(node);
            return;
        }
        if (node.Kind == SqlNodeKind.Server && node.Connected is null)
        {
            await ConnectAsync(node);
            return;
        }
        if (!node.Expands || (node.Children is null && !await ReadAsync(node)))
        {
            return;
        }
        node.IsExpanded = true;
        rows.Open(node, TreeRows<SqlNode>.Shown(node.Children!, n => n.Children));
    }

    /// <summary>Reads what is under a row from its server: true once read, false saying why not.</summary>
    private async Task<bool> ReadAsync(SqlNode node)
    {
        if (node.ServerRow.Connected is not { } server)
        {
            return false;
        }
        try
        {
            switch (node.Kind)
            {
                case SqlNodeKind.Server:
                    IReadOnlyList<string> databases = await BackgroundWork.Run(() => server.DatabasesAsync());
                    node.Children = [.. databases.Select(d => new SqlNode(SqlNodeKind.Database, d, 1, node))];
                    break;
                case SqlNodeKind.Database:
                    IReadOnlyList<SqlObject> objects = await BackgroundWork.Run(() => server.ObjectsAsync(node.Title));
                    node.Children = [Folder("Tables", SqlObjectKind.Table), Folder("Views", SqlObjectKind.View)];
                    SqlNode Folder(string title, SqlObjectKind kind)
                    {
                        SqlNode folder = new(SqlNodeKind.Folder, title, 2, node) { IsExpanded = kind == SqlObjectKind.Table };
                        folder.Children = [.. objects.Where(o => o.Kind == kind).Select(o => new SqlNode(SqlNodeKind.Object, o.Shown, 3, folder) { Object = o })];
                        folder.Detail = folder.Children.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        return folder;
                    }
                    break;
                case SqlNodeKind.Object:
                    SqlTableDesign design = await BackgroundWork.Run(() => server.DesignAsync(node.Database!, node.Object!));
                    node.Children = [.. design.Columns.Select(c => new SqlNode(SqlNodeKind.Column, c.Name, node.Depth + 1, node)
                    {
                        Detail = $"{(c.PrimaryKey ? "key, " : string.Empty)}{c.Type}, {(c.Nullable ? "null" : "not null")}",
                    })];
                    break;
            }
            return true;
        }
        catch (SqlFailure refused)
        {
            say(refused.Message);
            return false;
        }
    }

    /// <summary>Reads again what is under the chosen row's database, or its server where it is in none.</summary>
    public async Task RefreshAsync()
    {
        if (Chosen is not { } node)
        {
            return;
        }
        SqlNode from = node;
        while (from.Kind is not (SqlNodeKind.Server or SqlNodeKind.Database))
        {
            from = from.Holder!;
        }
        await RefreshAsync(from);
    }

    private async Task RefreshAsync(SqlNode from)
    {
        if (await ReadAsync(from))
        {
            from.IsExpanded = true;
            ShowTree();
            tree.SelectedItem = from;
        }
    }

    /// <summary>Aims the script at the chosen row's database, and lists where one can run.</summary>
    private void Aim()
    {
        List<SqlTarget> can = [];
        foreach (SqlNode server in servers.Where(s => s.Connected is not null))
        {
            can.Add(new SqlTarget(server, null));
            can.AddRange((server.Children ?? []).Select(d => new SqlTarget(server, d.Title)));
        }
        SqlTarget? wanted = Chosen is { ServerRow.Connected: not null } node ? new SqlTarget(node.ServerRow, node.Database) : Target;
        targets.ItemsSource = can;
        targets.SelectedItem = can.FirstOrDefault(t => t == wanted) ?? can.FirstOrDefault(t => t.Server == wanted?.Server) ?? can.FirstOrDefault();
    }

    private void Paint()
    {
        Aim();
        empty.Text = "No SQL Server yet";
        empty.Offer(AppActions.SqlConnect, "Connect to SQL Server…");
        empty.IsVisible = servers.Count == 0;
        tree.IsVisible = servers.Count > 0;
    }

    /// <summary>Runs the script written, or what of it is selected, where the bar says.</summary>
    public Task ExecuteAsync() => Target is { } target
        ? RunAsync(target, script.SelectedText is { Length: > 0 } selected ? selected : Script, null)
        : Task.CompletedTask;

    /// <summary>Opens the chosen table's or view's first rows, its script written in the box.</summary>
    public Task OpenDataAsync()
    {
        if (Chosen is not { Of: { } of } node)
        {
            return Task.CompletedTask;
        }
        Script = SqlScripts.SelectTop(of, OpenedRows);
        return RunAsync(new SqlTarget(node.ServerRow, node.Database), Script, of);
    }

    /// <summary>Stops the work under way: a script, a backup, a package.</summary>
    public void Stop() => running?.Cancel();

    private Task RunAsync(SqlTarget target, string text, SqlObject? of)
    {
        if (target.Server.Connected is not { } server)
        {
            return Task.CompletedTask;
        }
        SqlTableDesign? design = null;
        return WorkAsync("Running…", async stop =>
        {
            design = of is { Kind: SqlObjectKind.Table } ? await server.DesignAsync(target.Database!, of, stop) : null;
            return await server.RunAsync(target.Database, text, of is null ? MostRows : OpenedRows, stop);
        }, () =>
        {
            opened = of is null ? null : (target, of);
            return design;
        });
    }

    /// <summary>Does work on a server off the window's thread, one at a time, and shows what it came to: a script's results
    /// and messages, or a backup's or package's steps. <paramref name="design"/> gives, once done, the table whose rows a
    /// result is, where they can be written.</summary>
    private async Task<bool> WorkAsync(string doing, Func<CancellationToken, Task<SqlRun>> work, Func<SqlTableDesign?>? design = null)
    {
        if (Running)
        {
            return false;
        }
        using CancellationTokenSource stop = running = new();
        status.Text = doing;
        try
        {
            SqlRun run = await BackgroundWork.Run(() => work(stop.Token));
            Show(run, design?.Invoke());
            return !run.Failed;
        }
        catch (SqlFailure refused)
        {
            Show(new SqlRun([], [new SqlMessage(refused.Message, IsError: true)], TimeSpan.Zero, Failed: true), null);
            return false;
        }
        finally
        {
            running = null;
        }
    }

    /// <summary>The file dialog, as it is about to be shown: a test fills it.</summary>
    public event EventHandler<SqlFileDialog>? FileAsked;

    /// <summary>The database and the file the person named, read here on the window's thread; null where they cancelled.</summary>
    private async Task<(string Database, string File)?> AskFileAsync(SqlFileDialog dialog)
    {
        FileAsked?.Invoke(this, dialog);
        return await dialog.ShowDialog<bool>(owner) ? (dialog.Database, dialog.File) : null;
    }

    /// <summary>Backs the chosen row's database up to a file on the server's disk the person names.</summary>
    public async Task BackupAsync()
    {
        if (Chosen is not { Database: { } database } node || node.ServerRow.Connected is not { } server)
        {
            return;
        }
        try
        {
            string folder = await BackgroundWork.Run(() => server.BackupFolderAsync());
            if (await AskFileAsync(new SqlFileDialog($"Back Up {database}", "The backup is written by the server, to its own disk: the path is one of the server's machine, and a file there of that name is written over.",
                "Back Up", database, names: false, $"{folder}{database}.bak", local: null)) is { } asked)
            {
                await WorkAsync("Backing up…", stop => server.BackupAsync(database, asked.File, stop));
            }
        }
        catch (SqlFailure refused)
        {
            say(refused.Message);
        }
    }

    /// <summary>Restores a database of the chosen row's server from a backup on the server's disk, over the one of its name.</summary>
    public async Task RestoreAsync()
    {
        if (Chosen is not { } node || node.ServerRow.Connected is not { } server)
        {
            return;
        }
        try
        {
            string folder = await BackgroundWork.Run(() => server.BackupFolderAsync());
            string named = node.Database ?? string.Empty;
            if (await AskFileAsync(new SqlFileDialog("Restore Database", "The backup is read by the server, from its own disk. A database of this name is replaced by the backup, everyone connected to it disconnected first; a new name makes a new database. This cannot be undone.",
                "Restore", named, names: true, $"{folder}{(named.Length > 0 ? named : "Database")}.bak", local: null)) is { } asked
                && await WorkAsync("Restoring…", stop => server.RestoreAsync(asked.Database, asked.File, stop)))
            {
                await RefreshAsync(node.ServerRow);
            }
        }
        catch (SqlFailure refused)
        {
            say(refused.Message);
        }
    }

    /// <summary>Writes the chosen row's database, its schema and rows, to a .bacpac on this machine.</summary>
    public async Task ExportAsync()
    {
        if (Chosen is { Database: { } database } node && node.ServerRow.Connected is { } server
            && await AskFileAsync(new SqlFileDialog($"Export {database}", "The database's schema and rows are written to a .bacpac on this machine, which Import BACPAC, SQL Server Management Studio and Azure SQL read.",
                "Export", database, names: false, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"{database}.bacpac"), local: false)) is { } asked)
        {
            await WorkAsync("Exporting…", stop => server.ExportAsync(database, asked.File, stop));
        }
    }

    /// <summary>Makes a new database on the chosen row's server of a .bacpac on this machine.</summary>
    public async Task ImportAsync()
    {
        if (Chosen is { } node && node.ServerRow.Connected is { } server
            && await AskFileAsync(new SqlFileDialog("Import BACPAC", "A new database is made of a .bacpac on this machine: its schema, then its rows. The name must be one no database with tables of its own has.",
                "Import", string.Empty, names: true, string.Empty, local: true)) is { } asked
            && await WorkAsync("Importing…", stop => server.ImportAsync(asked.Database, asked.File, stop)))
        {
            await RefreshAsync(node.ServerRow);
        }
    }

    /// <summary>Shows a run: a tab a result, then the messages', the first result open, or the messages where there is none.</summary>
    private void Show(SqlRun? run, SqlTableDesign? design)
    {
        results = [.. (run?.Results ?? []).Select(r => new SqlRowsTable(r, design))];
        messages.Text = run is null ? string.Empty : string.Join('\n', run.Messages.Select(m => m.Line is { } line ? $"Line {line}: {m.Text}" : m.Text));
        tabs.Children.Clear();
        for (int i = 0; i <= results.Count; i++)
        {
            int at = i;
            ToggleButton tab = ToolTabs.Tab(i == results.Count ? MessagesTab : results.Count == 1 ? "Result" : $"Result {i + 1}", false);
            tab.Click += (_, _) => ShowTab(at);
            tabs.Children.Add(tab);
        }
        ShowTab(results.Count == 0 || run is { Failed: true, Results.Count: 0 } ? results.Count : 0);
        if (run is not null)
        {
            long read = results.Sum(r => (long)r.Rows.Rows.Count);
            string cut = results.Any(r => r.Rows.Cut) ? ", the first of more" : string.Empty;
            string took = run.Took.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            // Work that returns no rows (a backup, a package, a table made) says how it ended, not a count of none.
            status.Text = results.Count == 0 ? $"{(run.Failed ? "Failed" : "Done")}, {took} s"
                : $"{(run.Failed ? "Failed: " : string.Empty)}{read:N0} {Plural.Noun(read, "row")}{cut}, {took} s";
        }
    }

    private void ShowTab(int index)
    {
        shown = index;
        bool said = index >= results.Count;
        messagesPage.IsVisible = said;
        grid.IsVisible = !said;
        grid.Table = said ? null : results[index];
        for (int i = 0; i < tabs.Children.Count; i++)
        {
            ((ToggleButton)tabs.Children[i]).IsChecked = i == index;
        }
    }

    /// <summary>Reads the opened table's rows again, after a change to them.</summary>
    private Task ReopenAsync() => opened is { } was ? RunAsync(was.Target, SqlScripts.SelectTop(was.Opened, OpenedRows), was.Opened) : Task.CompletedTask;

    /// <summary>A cell written: the row's change by its key, or, in the row below the rows, a new row of the cells typed
    /// so far, which stay typed there while the server wants more of them.</summary>
    private async Task WriteAsync(CellWrite write)
    {
        if (write.Table is not SqlRowsTable { Written: { } design } table || opened is not { } at || at.Target.Server.Connected is not { } server)
        {
            return;
        }
        string column = design.Columns[write.Column].Name;
        try
        {
            foreach (int row in write.Rows)
            {
                if (table.IsNewRow(row))
                {
                    table.Pending[write.Column] = write.Text;
                    List<int> typed = [.. table.Pending.Keys.Order()];
                    await BackgroundWork.Run(() => server.ChangeAsync(at.Target.Database!, [SqlScripts.InsertRow(design, [.. typed.Select(c => design.Columns[c].Name)])],
                        [.. typed.Select((c, i) => new KeyValuePair<string, object>($"@v{i}", SqlValues.Written(table.Pending[c])))], rows: 1));
                }
                else
                {
                    await BackgroundWork.Run(() => server.ChangeAsync(at.Target.Database!, [SqlScripts.UpdateRow(design, column)],
                        [new("@v", SqlValues.Written(write.Text)), .. table.KeyOf(row)], rows: 1));
                }
            }
            await ReopenAsync();
        }
        catch (SqlFailure refused)
        {
            say(table.Pending.Count > 0 ? $"The new row is not added yet: {refused.Message}" : refused.Message);
            grid.InvalidateVisual();
        }
    }

    private async Task DeleteAsync(CellWrite write)
    {
        if (write.Table is not SqlRowsTable { Written: { } design } table || opened is not { } at || at.Target.Server.Connected is not { } server || Owner is not { } owner)
        {
            return;
        }
        List<int> gone = [.. write.Rows.Where(r => !table.IsNewRow(r))];
        if (gone.Count == 0 || !await new ConfirmDialog($"Delete {Plural.Noun(gone.Count, "Row")}?",
            $"{gone.Count:N0} {Plural.Noun(gone.Count, "row")} of {at.Opened.Shown} will be deleted on the server. This cannot be undone.", "Delete").ShowDialog<bool>(owner))
        {
            return;
        }
        try
        {
            // One change of every row, each told by its own key's parameters.
            List<string> statements = [.. gone.Select((_, i) => SqlScripts.DeleteRow(design).Replace("@k", $"@d{i}k", StringComparison.Ordinal))];
            List<KeyValuePair<string, object>> keys = [.. gone.SelectMany((row, i) => table.KeyOf(row).Select(k => new KeyValuePair<string, object>(k.Key.Replace("@k", $"@d{i}k", StringComparison.Ordinal), k.Value)))];
            await BackgroundWork.Run(() => server.ChangeAsync(at.Target.Database!, statements, keys, rows: gone.Count));
            await ReopenAsync();
        }
        catch (SqlFailure refused)
        {
            say(refused.Message);
        }
    }

    /// <summary>Designs a new table in the chosen row's database, or the chosen table; a view's script goes to the box.</summary>
    public async Task DesignAsync(bool isNew)
    {
        if (Chosen is not { Database: { } database } node || node.ServerRow.Connected is not { } server || Owner is not { } owner)
        {
            return;
        }
        try
        {
            if (!isNew && node.Of is { Kind: SqlObjectKind.View } view)
            {
                string? definition = await BackgroundWork.Run(() => server.DefinitionAsync(database, view));
                Script = definition is null ? $"-- {view.Shown} shows no script of its own" : SqlScripts.AlterView(definition);
                tree.SelectedItem = node;
                script.Focus();
                return;
            }
            SqlTableDesign? before = isNew ? null : await BackgroundWork.Run(() => server.DesignAsync(database, node.Of!));
            SqlTableDialog dialog = new(before, node.Of?.Schema ?? "dbo", async statements =>
            {
                try
                {
                    await BackgroundWork.Run(() => server.ChangeAsync(database, statements));
                    return null;
                }
                catch (SqlFailure refused)
                {
                    return refused.Message;
                }
            });
            DesignAsked?.Invoke(this, dialog);
            if (await dialog.ShowDialog<bool>(owner))
            {
                await RefreshAsync(DatabaseRow(node));
            }
        }
        catch (SqlFailure refused)
        {
            say(refused.Message);
        }
    }

    private static SqlNode DatabaseRow(SqlNode node) => node.Kind == SqlNodeKind.Database ? node : DatabaseRow(node.Holder!);

    /// <summary>Writes a new view's script in the box, aimed at the chosen row's database, for the person to finish and run.</summary>
    public void NewView()
    {
        if (Chosen is { Database: not null } node)
        {
            Script = SqlScripts.NewView(node.Of?.Schema ?? "dbo");
            Aim();
            script.Focus();
        }
    }

    /// <summary>Drops the chosen table or view, after a question.</summary>
    public async Task DropAsync()
    {
        if (Chosen is not { Of: { } of, Database: { } database } node || node.ServerRow.Connected is not { } server || Owner is not { } owner)
        {
            return;
        }
        string kind = of.Kind == SqlObjectKind.View ? "view" : "table";
        ConfirmDialog dialog = new($"Drop {of.Shown}?", $"The {kind} {of.Shown} of {database} will be dropped on the server{(of.Kind == SqlObjectKind.Table ? ", with every row in it" : string.Empty)}. This cannot be undone.", "Drop");
        DropAsked?.Invoke(this, dialog);
        if (!await dialog.ShowDialog<bool>(owner))
        {
            return;
        }
        try
        {
            await BackgroundWork.Run(() => server.ChangeAsync(database, [SqlScripts.Drop(of)]));
            await RefreshAsync(DatabaseRow(node));
        }
        catch (SqlFailure refused)
        {
            say(refused.Message);
        }
    }

    /// <summary>The question before a drop, as it is about to be shown: a test answers it.</summary>
    public event EventHandler<ConfirmDialog>? DropAsked;

    /// <summary>A row for whichever node the list hands its container: its chevron, its kind's icon, its title and detail.</summary>
    private Control Row()
    {
        RowIcon chevron = RowIcon.Chevron();
        Border toggle = new() { Width = 18, Height = ListRow.Height, Background = Brushes.Transparent, Child = chevron };
        RowIcon icon = new() { Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        TextBlock title = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock detail = MutedText.Of(string.Empty);
        detail.Margin = new Thickness(8, 0, 6, 0);
        detail.FontSize = 12;
        StackPanel line = new() { Orientation = Orientation.Horizontal, Height = ListRow.Height, Children = { toggle, icon, title, detail } };
        toggle.PointerPressed += (_, e) =>
        {
            if (line.DataContext is SqlNode node)
            {
                IdeErrors.Observe(ToggleAsync(node));
                e.Handled = true;
            }
        };
        return line.Showing<SqlNode>(node =>
        {
            line.Margin = new Thickness(node.Depth * 14, 0, 0, 0);
            chevron.IsVisible = node.Expands;
            chevron.Turned = node.IsExpanded;
            icon.DataKey = node.Kind switch
            {
                SqlNodeKind.Server => IconKeys.SqlServer,
                SqlNodeKind.Database => IconKeys.Database,
                SqlNodeKind.Folder => IconKeys.Explorer,
                SqlNodeKind.Object => node.Object!.Kind == SqlObjectKind.View ? IconKeys.Preview : IconKeys.Table,
                _ => IconKeys.Variable,
            };
            title.Text = node.Title;
            detail.Text = node.Detail;
        });
    }
}
