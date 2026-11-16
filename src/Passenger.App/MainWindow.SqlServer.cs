using Passenger.App.Sql;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>The SQL Server window: the servers the person connects to (kept in the store, never a password), what each
/// holds, a script run on one and its results, a table's rows opened and written, and a table or view made, changed and
/// dropped, a database backed up and restored (<see cref="SqlServerView"/>). Its view is built as it is first shown or as one of its actions is asked for;
/// a connection lasts while the window does.</summary>
public partial class MainWindow
{
    private SqlServerView? sqlView;

    /// <summary>The SQL Server window's view, built as it is first shown or asked for.</summary>
    public SqlServerView SqlServerWindow => sqlView ?? (SqlServerView)Dock[ToolWindowIds.SqlServer].Content;

    private void AddSqlServer()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.SqlServer, "SQL Server", IconKeys.SqlServer, () =>
        {
            SqlServerView view = sqlView = new SqlServerView(
                () => state?.SqlServers.Value ?? new SqlServersState(),
                change =>
                {
                    if (state is { } store)
                    {
                        store.SqlServers.Set(change(store.SqlServers.Value));
                    }
                },
                action => Request(action), said => Say(said), this);
            ActionButtons.ShowMarked(view, actions!);
            return view;
        }, Docking.ToolAnchor.BottomLeft)
        {
            Action = A.ShowSqlServer,
            Menus = [new MenuPlace("Tools", 0, 11)],
        });
        Sql(A.SqlConnect, "Connect to SQL Server…", view => view.ConnectAsync(), 0, 0, shows: true);
        Sql(A.SqlDisconnect, "Disconnect from SQL Server", view =>
        {
            view.Disconnect();
            return Task.CompletedTask;
        }, 0, 1);
        Sql(A.SqlForget, "Forget SQL Server", view =>
        {
            view.Forget();
            return Task.CompletedTask;
        }, 0, 2);
        Sql(A.SqlRefresh, "Refresh SQL Server", view => view.RefreshAsync(), 0, 3);
        Sql(A.SqlExecute, "Execute SQL Script", view => view.ExecuteAsync(), 1, 0);
        Sql(A.SqlStop, "Stop SQL Script", view =>
        {
            view.Stop();
            return Task.CompletedTask;
        }, 1, 1);
        Sql(A.SqlOpenData, "Open Table or View Rows", view => view.OpenDataAsync(), 2, 0);
        Sql(A.SqlNewTable, "New SQL Table…", view => view.DesignAsync(isNew: true), 2, 1);
        Sql(A.SqlNewView, "New SQL View", view =>
        {
            view.NewView();
            return Task.CompletedTask;
        }, 2, 2);
        Sql(A.SqlDesign, "Design SQL Table or Edit View…", view => view.DesignAsync(isNew: false), 2, 3);
        Sql(A.SqlDrop, "Drop SQL Table or View…", view => view.DropAsync(), 2, 4);
        Sql(A.SqlBackup, "Back Up SQL Database…", view => view.BackupAsync(), 3, 0);
        Sql(A.SqlRestore, "Restore SQL Database…", view => view.RestoreAsync(), 3, 1);
        Sql(A.SqlExport, "Export SQL Database to BACPAC…", view => view.ExportAsync(), 3, 2);
        Sql(A.SqlImport, "Import SQL Database from BACPAC…", view => view.ImportAsync(), 3, 3);
    }

    /// <summary>One of the SQL Server window's actions: run on its view, refused in the view's words, listed under Tools.</summary>
    private void Sql(string id, string title, Func<SqlServerView, Task> run, int group, int order, bool shows = false) =>
        actions!.Add(new IdeAction(id, title, "SQL Server", _ =>
        {
            if (shows && !Dock.IsShown(ToolWindowIds.SqlServer))
            {
                ShowToolWindow(ToolWindowIds.SqlServer);
            }
            IdeErrors.Observe(run(SqlServerWindow));
        })
        {
            WhyNot = _ => shows ? null : sqlView is null ? "The SQL Server window has not been opened" : sqlView.WhyNot(id),
            Menus = [new MenuPlace("Tools/SQL Server", group, order)],
            Words = ["Database", "T-SQL", "MSSQL"],
        });
}
