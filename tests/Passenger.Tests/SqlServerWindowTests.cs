using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Sql;
using Passenger.Sql;

namespace Passenger.Tests;

/// <summary>The SQL Server window against a SQL Server, driven through its actions and its dialogs as a person drives
/// them: a connection made, a table designed, its rows opened, a row added and changed in the grid, the database
/// exported to a .bacpac and imported, backed up and restored, the table dropped.
/// It needs a server to talk to and pulls no image, so it skips saying so where none is named: MSSQL_TEST_SERVER its
/// address ("127.0.0.1,14333") and MSSQL_SA_PASSWORD its sa login's password. The scripts themselves are held by the
/// unit tests (SqlScriptsTests).</summary>
public class SqlServerWindowTests
{
    private const string NoServer = "Needs a SQL Server to talk to, which no test pulls or starts: name one in MSSQL_TEST_SERVER (its address) and MSSQL_SA_PASSWORD (its sa password)";

    [AvaloniaFact]
    public void A_table_is_designed_its_rows_written_in_the_grid_and_it_is_dropped_on_a_server_connected_to()
    {
        string? address = Environment.GetEnvironmentVariable("MSSQL_TEST_SERVER");
        string? password = Environment.GetEnvironmentVariable("MSSQL_SA_PASSWORD");
        Assert.SkipWhen(string.IsNullOrEmpty(address) || string.IsNullOrEmpty(password), NoServer);
        string database = $"PassengerTest{Guid.NewGuid():N}";
        string folder = TestFolders.Create("sql-");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "no code here\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ShowToolWindow(ToolWindowIds.SqlServer);
            SqlServerView view = window.SqlServerWindow;
            SqlConnectionInfo server = new("Test", address!, SqlSignIn.Login, "sa", TrustCertificate: true);
            SqlConnectDialog? connecting = null;
            view.ConnectAsked += (_, dialog) =>
            {
                connecting = dialog;
                dialog.Opened += (_, _) => IdeErrors.Observe(dialog.FillAsync(server, password));
            };
            Assert.True(window.Perform(AppActions.SqlConnect), "Connect to SQL Server did not run");
            Ui.WaitFor(() => view.Tree.Contains("  master"), () => $"the server's databases in the tree, which shows [{string.Join(" | ", view.Tree)}] and whose dialog says: {connecting?.Problem}",
                TimeSpan.FromSeconds(60));
            Assert.Equal("Test", view.Tree[0]);

            Run(window, view, $"CREATE DATABASE [{database}]");
            Assert.True(view.Choose("Test"), "The server's row was not there to choose");
            Assert.True(window.Perform(AppActions.SqlRefresh));
            Ui.WaitFor(() => view.Tree.Contains($"  {database}"), () => $"the new database in the tree, which shows [{string.Join(" | ", view.Tree)}]");

            Assert.True(view.Choose(database));
            view.DesignAsked += (_, dialog) => dialog.Opened += (_, _) =>
            {
                dialog.TableName = "Customer";
                dialog.Design([new(null, new SqlColumn("Id", "int", Nullable: false, Identity: true, PrimaryKey: true)), new(null, new SqlColumn("Name", "nvarchar(50)", Nullable: false))]);
                Assert.Contains("CREATE TABLE [dbo].[Customer]", dialog.Script);
                IdeErrors.Observe(dialog.ApplyAsync());
            };
            Assert.True(window.Perform(AppActions.SqlNewTable), "New SQL Table did not run on a database's row");
            Ui.WaitFor(() => view.Tree.Contains("      Customer"), () => $"the new table in the tree, which shows [{string.Join(" | ", view.Tree)}]");

            Assert.True(view.Choose("Customer"));
            Assert.True(window.Perform(AppActions.SqlOpenData));
            Ui.WaitFor(() => view.Result is { Written: not null }, () => $"the table's rows, to write; the window says: {view.Status} {view.Messages}");
            Assert.Equal("SELECT TOP (1000) * FROM [dbo].[Customer]", view.Script);
            Assert.Equal((1, true), (view.Result!.RowCount, view.Result.IsNewRow(0)));
            Assert.False(view.Result.IsEditable(0, 0), "An identity column, which the server numbers, could be written");

            Write(window, view, 0, 1, "Ada");
            Ui.WaitFor(() => view.Result is { RowCount: 2 } rows && rows.Cell(0, 1) == "Ada", () => $"the row added, where the grid shows {view.Result?.RowCount} rows and the window says: {view.Status} {view.Messages}");
            Assert.Equal("1", view.Result!.Cell(0, 0));
            Write(window, view, 0, 1, "Bob");
            Ui.WaitFor(() => view.Result is { RowCount: 2 } rows && rows.Cell(0, 1) == "Bob", () => $"the row changed, where its cell says {view.Result?.Cell(0, 1)}");

            Run(window, view, "SELECT Name FROM dbo.Customer\nGO\nSELEC nothing");
            Assert.Contains("Line 3: ", view.Messages);
            Assert.StartsWith("Failed: 1 row", view.Status);

            // The database to a .bacpac here and a new one made of it; then backed up on the server and restored under another name.
            string package = Path.Combine(folder, "shop.bacpac");
            Action<SqlFileDialog> fill = dialog => dialog.File = package;
            view.FileAsked += (_, dialog) => dialog.Opened += (_, _) =>
            {
                fill(dialog);
                dialog.Close(true);
            };
            Assert.True(view.Choose(database));
            Assert.True(window.Perform(AppActions.SqlExport), $"Export to BACPAC did not run: {view.WhyNot(AppActions.SqlExport)}");
            Ui.WaitFor(() => !view.Running && view.Status.StartsWith("Done", StringComparison.Ordinal) && File.Exists(package), () => $"the database exported; the window says: {view.Status} {view.Messages}",
                TimeSpan.FromMinutes(2));
            fill = dialog =>
            {
                dialog.Database = database + "Imported";
                dialog.File = package;
            };
            Assert.True(window.Perform(AppActions.SqlImport), $"Import from BACPAC did not run: {view.WhyNot(AppActions.SqlImport)}");
            Ui.WaitFor(() => view.Tree.Contains($"  {database}Imported"), () => $"the imported database in the tree; the window says: {view.Status} {view.Messages}", TimeSpan.FromMinutes(2));

            fill = _ => { };
            Assert.True(view.Choose(database));
            Assert.True(window.Perform(AppActions.SqlBackup), $"Back Up did not run: {view.WhyNot(AppActions.SqlBackup)}");
            Ui.WaitFor(() => view.Messages.Contains("BACKUP DATABASE successfully", StringComparison.Ordinal), () => $"the database backed up; the window says: {view.Status} {view.Messages}", TimeSpan.FromMinutes(2));
            fill = dialog => dialog.Database = database + "Restored";
            Assert.True(view.Choose(database));
            Assert.True(window.Perform(AppActions.SqlRestore), $"Restore did not run: {view.WhyNot(AppActions.SqlRestore)}");
            Ui.WaitFor(() => view.Tree.Contains($"  {database}Restored"), () => $"the restored database in the tree; the window says: {view.Status} {view.Messages}", TimeSpan.FromMinutes(2));
            Assert.True(view.Choose($"{database}Restored"));
            Run(window, view, "SELECT Name FROM dbo.Customer");
            Assert.Equal("Bob", view.Result!.Cell(0, 0));

            // The server's rows were read again, closed: the database is opened as a click on its chevron opens it.
            Assert.True(view.Choose(database));
            IdeErrors.Observe(view.ToggleAsync(view.Chosen!));
            Ui.WaitFor(() => view.Tree.Contains("      Customer"), () => $"the database's tables in the tree, which shows [{string.Join(" | ", view.Tree)}]");
            Assert.True(view.Choose("Customer"));
            view.DropAsked += (_, dialog) => dialog.Opened += (_, _) => dialog.Close(true);
            Assert.True(window.Perform(AppActions.SqlDrop));
            Ui.WaitFor(() => !view.Tree.Contains("      Customer") && view.Tree.Contains($"  {database}"), () => $"the table gone from the tree, which shows [{string.Join(" | ", view.Tree)}]");
        }
        finally
        {
            if (window.SqlServerWindow is { Target: not null } still)
            {
                Run(window, still, "USE master\nGO\n" + string.Join("\nGO\n", new[] { string.Empty, "Imported", "Restored" }.Select(made =>
                    $"IF DB_ID('{database}{made}') IS NOT NULL BEGIN ALTER DATABASE [{database}{made}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}{made}] END")));
            }
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    private static void Run(MainWindow window, SqlServerView view, string script)
    {
        view.Script = script;
        Assert.True(window.Perform(AppActions.SqlExecute), $"Execute SQL Script did not run: {view.WhyNot(AppActions.SqlExecute)}");
        Ui.WaitFor(() => !view.Running, () => $"the script to end: {script}", TimeSpan.FromSeconds(60));
    }

    /// <summary>Writes a cell as a person does: chosen, written in its box, Enter.</summary>
    private static void Write(MainWindow window, SqlServerView view, int row, int column, string text)
    {
        view.Grid.Choose((row, column), (row, column));
        view.Grid.Focus();
        Assert.True(view.Grid.BeginWriting(), $"The cell at row {row}, column {column} did not open to write in");
        view.Grid.Writing!.Text = text;
        Ui.Press(window, "ENTER");
    }
}
