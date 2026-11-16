using Passenger.Sql;

namespace Passenger.UnitTests.Sql;

/// <summary>The T-SQL Passenger writes, as text: names in brackets whatever is in them, a table made and changed from its
/// design in the order SQL Server takes the statements, a design it has no statement for refused in words, a row's
/// change told by its key's parameters, a script cut at its GO lines, and values shown as they can be typed again.</summary>
public class SqlScriptsTests
{
    private static readonly SqlTableDesign Customer = new("dbo", "Customer",
    [
        new("Id", "int", Nullable: false, Identity: true, PrimaryKey: true),
        new("Name", "nvarchar(50)", Nullable: false),
        new("Born", "date"),
        new("Credit", "decimal(18, 2)", Nullable: false, Default: "0") { DefaultConstraint = "DF_Credit" },
    ])
    {
        KeyConstraint = "PK_Customer",
    };

    private static SqlColumnEdit Kept(int column, Func<SqlColumn, SqlColumn>? change = null) =>
        new(Customer.Columns[column].Name, (change ?? (c => c))(Customer.Columns[column]));

    [Fact]
    public void A_name_goes_in_brackets_with_its_own_closing_bracket_doubled()
    {
        Assert.Equal("[dbo].[Order Details]", SqlScripts.Name("dbo", "Order Details"));
        Assert.Equal("[a]]; DROP TABLE x --]", SqlScripts.Name("a]; DROP TABLE x --"));
        Assert.Equal("N'O''Brien'", SqlScripts.Literal("O'Brien"));
        Assert.Equal("SELECT TOP (1000) * FROM [dbo].[Customer]", SqlScripts.SelectTop(new SqlObject(SqlObjectKind.Table, "dbo", "Customer"), 1000));
        Assert.Equal("DROP VIEW [sales].[Rich]", SqlScripts.Drop(new SqlObject(SqlObjectKind.View, "sales", "Rich")));
    }

    [Fact]
    public void A_new_table_is_its_columns_and_its_key()
    {
        Assert.Equal("""
            CREATE TABLE [dbo].[Customer]
            (
                [Id] int IDENTITY(1, 1) NOT NULL,
                [Name] nvarchar(50) NOT NULL,
                [Born] date NULL,
                [Credit] decimal(18, 2) NOT NULL DEFAULT (0),
                CONSTRAINT [PK_Customer] PRIMARY KEY ([Id])
            )
            """.ReplaceLineEndings("\n"), SqlScripts.CreateTable(Customer));
    }

    [Theory]
    [InlineData("", "int", false, "Every column needs a name")]
    [InlineData("Id", "", false, "Id needs a type")]
    [InlineData("Id", "int", true, "Id is of the primary key, whose columns take no NULL")]
    public void A_design_that_makes_no_table_is_refused_in_words(string name, string type, bool nullable, string why)
    {
        SqlDesignException refused = Assert.Throws<SqlDesignException>(() =>
            SqlScripts.CreateTable(new SqlTableDesign("dbo", "T", [new SqlColumn(name, type, nullable, PrimaryKey: true)])));
        Assert.Equal(why, refused.Message);
        Assert.Equal("Two columns are named a", Assert.Throws<SqlDesignException>(() =>
            SqlScripts.CreateTable(new SqlTableDesign("dbo", "T", [new SqlColumn("a", "int"), new SqlColumn("A", "int")]))).Message);
        Assert.Equal("A table needs at least one column", Assert.Throws<SqlDesignException>(() => SqlScripts.CreateTable(new SqlTableDesign("dbo", "T", []))).Message);
    }

    [Fact]
    public void A_design_left_as_it_was_read_changes_nothing() =>
        Assert.Empty(SqlScripts.AlterTable(Customer, "Customer", [Kept(0), Kept(1), Kept(2), Kept(3 , c => c with { Default = "((0))", Type = "DECIMAL (18,2)" })]));

    [Fact]
    public void A_changed_table_drops_renames_retypes_and_adds_in_the_order_the_server_takes_them()
    {
        IReadOnlyList<string> script = SqlScripts.AlterTable(Customer, "Client",
        [
            Kept(0),
            Kept(1, c => c with { Name = "FullName", Type = "nvarchar(80)" }),
            Kept(3, c => c with { Default = "5", Nullable = true }),
            new(null, new SqlColumn("Email", "varchar(100)")),
        ]);
        Assert.Equal(
        [
            "ALTER TABLE [dbo].[Customer] DROP COLUMN [Born]",
            "EXEC sp_rename N'dbo.Customer.Name', N'FullName', N'COLUMN'",
            "ALTER TABLE [dbo].[Customer] ALTER COLUMN [FullName] nvarchar(80) NOT NULL",
            "ALTER TABLE [dbo].[Customer] DROP CONSTRAINT [DF_Credit]",
            "ALTER TABLE [dbo].[Customer] ALTER COLUMN [Credit] decimal(18, 2) NULL",
            "ALTER TABLE [dbo].[Customer] ADD DEFAULT (5) FOR [Credit]",
            "ALTER TABLE [dbo].[Customer] ADD [Email] varchar(100) NULL",
            "EXEC sp_rename N'dbo.Customer', N'Client'",
        ], script);
    }

    [Fact]
    public void A_changed_key_is_let_go_of_by_its_constraints_name_and_made_again()
    {
        IReadOnlyList<string> script = SqlScripts.AlterTable(Customer, "Customer", [Kept(0, c => c with { PrimaryKey = false }), Kept(1, c => c with { PrimaryKey = true }), Kept(2), Kept(3)]);
        Assert.Equal(["ALTER TABLE [dbo].[Customer] DROP CONSTRAINT [PK_Customer]", "ALTER TABLE [dbo].[Customer] ADD CONSTRAINT [PK_Customer] PRIMARY KEY ([Name])"], script);
        Assert.Equal("Id: SQL Server cannot make an existing column an identity, or stop it being one",
            Assert.Throws<SqlDesignException>(() => SqlScripts.AlterTable(Customer, "Customer", [Kept(0, c => c with { Identity = false }), Kept(1), Kept(2), Kept(3)])).Message);
    }

    [Fact]
    public void A_rows_change_names_its_key_by_parameters_never_by_values()
    {
        SqlTableDesign two = new("dbo", "Line", [new("Order", "int", false, PrimaryKey: true), new("No", "int", false, PrimaryKey: true), new("Qty", "int")]);
        Assert.Equal("UPDATE [dbo].[Line] SET [Qty] = @v WHERE [Order] = @k0 AND [No] = @k1", SqlScripts.UpdateRow(two, "Qty"));
        Assert.Equal("DELETE FROM [dbo].[Line] WHERE [Order] = @k0 AND [No] = @k1", SqlScripts.DeleteRow(two));
        Assert.Equal("INSERT INTO [dbo].[Line] ([Order], [Qty]) VALUES (@v0, @v1)", SqlScripts.InsertRow(two, ["Order", "Qty"]));
        Assert.Equal("INSERT INTO [dbo].[Line] DEFAULT VALUES", SqlScripts.InsertRow(two, []));
    }

    [Fact]
    public void A_script_is_cut_at_its_GO_lines_each_batch_with_its_line_but_not_inside_a_comment_or_a_string()
    {
        IReadOnlyList<SqlBatch> batches = SqlScripts.Batches("SELECT 1\ngo\n\nSELECT 'a\nGO\nb'\n/* \nGO\n*/ -- GO\nGO;\n  GO  \nSELECT 3\n");
        Assert.Equal([new SqlBatch("SELECT 1", 1), new SqlBatch("\nSELECT 'a\nGO\nb'\n/* \nGO\n*/ -- GO", 3), new SqlBatch("SELECT 3", 12)], batches);
        Assert.Empty(SqlScripts.Batches("  \nGO\n"));
    }

    [Fact]
    public void A_backup_and_a_restore_name_the_servers_files_and_let_everyone_in_again()
    {
        Assert.Equal("BACKUP DATABASE [Shop] TO DISK = N'/var/opt/mssql/data/Shop''s.bak' WITH INIT, COPY_ONLY, STATS = 10", SqlScripts.Backup("Shop", "/var/opt/mssql/data/Shop's.bak"));
        string restore = SqlScripts.Restore("Copy", @"C:\b\Shop.bak", [("Shop", false), ("Shop_log", true), ("More", false)], @"C:\data", @"C:\log\");
        Assert.Equal("""
            IF DB_ID(N'Copy') IS NOT NULL ALTER DATABASE [Copy] SET SINGLE_USER WITH ROLLBACK IMMEDIATE
            GO
            RESTORE DATABASE [Copy] FROM DISK = N'C:\b\Shop.bak' WITH REPLACE, STATS = 10, MOVE N'Shop' TO N'C:\data\Copy.mdf', MOVE N'Shop_log' TO N'C:\log\Copy_log.ldf', MOVE N'More' TO N'C:\data\Copy_1.ndf'
            GO
            IF DB_ID(N'Copy') IS NOT NULL ALTER DATABASE [Copy] SET MULTI_USER

            """.ReplaceLineEndings("\n"), restore);
        Assert.Equal(3, SqlScripts.Batches(restore).Count);
        Assert.Equal("/var/opt/mssql/data/", SqlScripts.In("/var/opt/mssql/data"));
    }

    [Fact]
    public void A_views_script_is_turned_to_one_that_changes_it() =>
        Assert.Equal("-- mine\nALTER VIEW dbo.Rich AS SELECT 1 AS [create]", SqlScripts.AlterView("-- mine\ncreate VIEW dbo.Rich AS SELECT 1 AS [create]"));

    [Fact]
    public void A_type_is_written_with_the_length_its_kind_takes_and_a_value_as_it_can_be_typed_again()
    {
        Assert.Equal("nvarchar(50)", SqlTypes.Written("nvarchar", 100, 0, 0));
        Assert.Equal("varchar(max)", SqlTypes.Written("varchar", -1, 0, 0));
        Assert.Equal("decimal(18, 2)", SqlTypes.Written("decimal", 9, 18, 2));
        Assert.Equal("datetime2", SqlTypes.Written("datetime2", 8, 27, 7));
        Assert.Equal("datetime2(3)", SqlTypes.Written("datetime2", 7, 23, 3));
        Assert.Equal("int", SqlTypes.Written("int", 4, 10, 0));
        Assert.Equal("NULL", SqlValues.Shown(null));
        Assert.Equal("12.50", SqlValues.Shown(12.50m));
        Assert.Equal("1", SqlValues.Shown(true));
        Assert.Equal("1815-12-10", SqlValues.Shown(new DateTime(1815, 12, 10)));
        Assert.Equal("2026-01-02 03:04:05", SqlValues.Shown(new DateTime(2026, 1, 2, 3, 4, 5)));
        Assert.Equal("0x0AFF", SqlValues.Shown(new byte[] { 10, 255 }));
        Assert.Equal(DBNull.Value, SqlValues.Written("NULL"));
        Assert.Equal("null", SqlValues.Written("null"));
    }
}
