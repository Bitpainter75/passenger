using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;
using Passenger.Sql;

namespace Passenger.App.Sql;

/// <summary>A table's design: its name and its columns in their order, each with its type, whether it takes NULL, is of
/// the primary key and numbers itself, and its default; a column added at the end or taken out. Under them the script
/// the design comes to, as it is changed: CREATE TABLE for a new table, for one there the statements that turn it into
/// what is shown, or why SQL Server has none for it. Its button runs the script as one change and closes once the server
/// took it, the server's own words shown where it did not.</summary>
public sealed class SqlTableDialog : IdeWindow
{
    private const string Columns = "*,150,Auto,Auto,Auto,110,Auto";

    private readonly SqlTableDesign? before;
    private readonly string schema;
    private readonly Func<IReadOnlyList<string>, Task<string?>> apply;
    private readonly TextBox name = new();
    private readonly StackPanel rows = new() { Spacing = 4 };
    private readonly SelectableTextBlock script = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock problem = new() { Classes = { StyleClasses.Error }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button ok;
    private IReadOnlyList<string> statements = [];
    private bool applying;

    /// <param name="before">The table as read, to change; null for a new one in <paramref name="schema"/>.</param>
    /// <param name="apply">Runs the script as one change, answering null once done, else why not.</param>
    public SqlTableDialog(SqlTableDesign? before, string schema, Func<IReadOnlyList<string>, Task<string?>> apply)
    {
        this.before = before;
        this.schema = schema;
        this.apply = apply;
        FormRows.Scope(this);
        Title = before is null ? "New Table" : $"Design {before.Name}";
        Width = 760;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ok = DialogFooter.Default(before is null ? "Create" : "Apply");
        ok.Click += (_, _) => IdeErrors.Observe(ApplyAsync());
        name.Text = before?.Name ?? string.Empty;
        name.TextChanged += (_, _) => Paint();
        foreach (SqlColumn column in before?.Columns ?? [new SqlColumn("Id", "int", Nullable: false, Identity: true, PrimaryKey: true)])
        {
            rows.Children.Add(new Row(this, before is null ? null : column.Name, column));
        }
        Button add = new() { Content = "Add Column", HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) =>
        {
            Row added = new(this, null, new SqlColumn(string.Empty, "nvarchar(50)"));
            rows.Children.Add(added);
            added.Name.Focus();
            Paint();
        };
        Grid titles = new() { ColumnDefinitions = new ColumnDefinitions(Columns), ColumnSpacing = 8 };
        foreach ((string title, int at) in new[] { "Column", "Type", "Null", "Key", "Identity", "Default" }.Select((t, i) => (t, i)))
        {
            TextBlock header = MutedText.Of(title);
            header.FontSize = 12;
            header.MinWidth = at is 2 or 3 or 4 ? 54 : 0;
            Grid.SetColumn(header, at);
            titles.Children.Add(header);
        }
        titles.Children.Add(new Border { Width = 28, [Grid.ColumnProperty] = 6 });
        Border scripted = new() { Classes = { StyleClasses.Code }, Height = 150, Child = new ScrollViewer { Content = script.InCodeFont() }, [DockPanel.DockProperty] = Dock.Bottom, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel page = new()
        {
            Children =
            {
                new StackPanel { [DockPanel.DockProperty] = Dock.Top, Spacing = 10, Children = { FormRows.Of("Table:", name), titles } },
                scripted,
                new Border { [DockPanel.DockProperty] = Dock.Bottom, Margin = new Thickness(0, 8, 0, 0), Child = add },
                new ScrollViewer { Content = rows, Margin = new Thickness(0, 4, 0, 0) },
            },
        };
        Content = DialogFooter.Page(page, DialogFooter.OkCancel(ok, () => Close(false), problem));
        Opened += (_, _) => name.Focus();
        Paint();
    }

    /// <summary>The script the design comes to, a statement a line; empty while it comes to none.</summary>
    public string Script => script.Text ?? string.Empty;

    /// <summary>Why the design has no script, or the server refused it; null while neither.</summary>
    public string? Problem => problem.Text is { Length: > 0 } said ? said : null;

    /// <summary>The table's name as typed.</summary>
    public string TableName
    {
        get => name.Text ?? string.Empty;
        set => name.Text = value;
    }

    /// <summary>The columns as designed, each with the name it had in the table as read.</summary>
    public IReadOnlyList<SqlColumnEdit> Edits => [.. rows.Children.OfType<Row>().Select(r => r.Edit)];

    /// <summary>Puts the columns given in the designer's place, as a person typing them would, each by the name it had.</summary>
    public void Design(IReadOnlyList<SqlColumnEdit> columns)
    {
        rows.Children.Clear();
        rows.Children.AddRange(columns.Select(c => new Row(this, c.Was, c.Now)));
        Paint();
    }

    /// <summary>Runs the script, as the dialog's button does.</summary>
    public Task ApplyAsync() => applying || statements.Count == 0 ? Task.CompletedTask : ApplyingAsync();

    private async Task ApplyingAsync()
    {
        applying = true;
        ok.IsEnabled = false;
        string? refused = await apply(statements);
        applying = false;
        if (refused is null)
        {
            Close(true);
            return;
        }
        problem.Text = refused;
        ok.IsEnabled = true;
    }

    /// <summary>Writes the script the design comes to now, or why it comes to none.</summary>
    private void Paint()
    {
        try
        {
            statements = before is null
                ? [SqlScripts.CreateTable(new SqlTableDesign(schema, TableName.Trim(), [.. Edits.Select(e => e.Now)]))]
                : SqlScripts.AlterTable(before, TableName, Edits);
            script.Text = string.Join('\n', statements);
            problem.Text = string.Empty;
        }
        catch (SqlDesignException refused)
        {
            statements = [];
            script.Text = string.Empty;
            problem.Text = refused.Message;
        }
        ok.IsEnabled = !applying && statements.Count > 0;
    }

    /// <summary>A column's row in the designer.</summary>
    private sealed class Row : Grid
    {
        private readonly string? was;
        private readonly SqlColumn read;
        private readonly TextBox type = new();
        private readonly CheckBox nullable = new() { HorizontalAlignment = HorizontalAlignment.Center };
        private readonly CheckBox key = new() { HorizontalAlignment = HorizontalAlignment.Center };
        private readonly CheckBox identity = new() { HorizontalAlignment = HorizontalAlignment.Center };
        private readonly TextBox value = new();

        public Row(SqlTableDialog dialog, string? was, SqlColumn column)
        {
            this.was = was;
            read = column;
            ColumnDefinitions = new ColumnDefinitions(Columns);
            ColumnSpacing = 8;
            Name.Text = column.Name;
            type.Text = column.Type;
            type.PlaceholderText = "int, nvarchar(50), date…";
            ToolTip.SetTip(type, $"A SQL Server type, as a script writes it: {string.Join(", ", SqlTypes.Common)}");
            nullable.IsChecked = column.Nullable;
            key.IsChecked = column.PrimaryKey;
            identity.IsChecked = column.Identity;
            value.Text = column.Default ?? string.Empty;
            Button remove = ToolButtons.Of(IconKeys.Close, "Remove Column", () =>
            {
                ((Panel)Parent!).Children.Remove(this);
                dialog.Paint();
            });
            Control[] cells = [Name, type, Centred(nullable), Centred(key), Centred(identity), value, remove];
            for (int i = 0; i < cells.Length; i++)
            {
                SetColumn(cells[i], i);
                Children.Add(cells[i]);
            }
            Name.TextChanged += (_, _) => dialog.Paint();
            type.TextChanged += (_, _) => dialog.Paint();
            value.TextChanged += (_, _) => dialog.Paint();
            nullable.IsCheckedChanged += (_, _) => dialog.Paint();
            identity.IsCheckedChanged += (_, _) => dialog.Paint();
            key.IsCheckedChanged += (_, _) =>
            {
                // A key's column takes no NULL, so choosing it for the key says so.
                if (key.IsChecked == true)
                {
                    nullable.IsChecked = false;
                }
                dialog.Paint();
            };
        }

        public new TextBox Name { get; } = new();

        public SqlColumnEdit Edit => new(was, read with
        {
            Name = (Name.Text ?? string.Empty).Trim(),
            Type = (type.Text ?? string.Empty).Trim(),
            Nullable = nullable.IsChecked == true,
            PrimaryKey = key.IsChecked == true,
            Identity = identity.IsChecked == true,
            Default = string.IsNullOrWhiteSpace(value.Text) ? null : value.Text.Trim(),
        });

        private static Border Centred(Control box) => new() { MinWidth = 54, Child = box };
    }
}
