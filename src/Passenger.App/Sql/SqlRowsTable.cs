using Passenger.App.Tables;
using Passenger.Core;
using Passenger.Sql;

namespace Passenger.App.Sql;

/// <summary>A statement's rows as a table. The rows of a table opened whole, one with a primary key, are written in
/// place: a cell but a column's the server writes itself, a row standing empty below them for a new one, whose cells
/// are kept here as typed (<see cref="Pending"/>) until the server takes the row. Any other result is read only.</summary>
public sealed class SqlRowsTable(SqlRows rows, SqlTableDesign? written = null) : Table
{
    /// <summary>The rows as read.</summary>
    public SqlRows Rows { get; } = rows;

    /// <summary>The table whose rows these are, where they can be written.</summary>
    public SqlTableDesign? Written { get; } = written is { } design && design.Key.Any() && rows.Columns.Select(c => c.Name).SequenceEqual(design.Columns.Select(c => c.Name)) ? design : null;

    /// <summary>The new row's cells typed so far, by column.</summary>
    public Dictionary<int, string> Pending { get; } = [];

    public override int RowCount => Rows.Rows.Count + (Written is null ? 0 : 1);

    public override int ColumnCount => Rows.Columns.Count;

    public override string Header(int column) => Rows.Columns[column].Name;

    public override string Cell(int row, int column) =>
        IsNewRow(row) ? Pending.GetValueOrDefault(column, string.Empty) : SqlValues.Shown(Rows.Rows[row][column]);

    public override bool IsNumber(int row, int column) => !IsNewRow(row) && SqlValues.IsNumber(Rows.Rows[row][column]);

    public override CellValue ValueOf(int row, int column) =>
        IsNewRow(row) || Rows.Rows[row][column] is not { } value ? new(CellKind.Empty)
        : SqlValues.IsNumber(value) ? new(CellKind.Number, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)) : new(CellKind.Text);

    public override string RowLabel(int row) => IsNewRow(row) ? "+" : base.RowLabel(row);

    public override bool IsNewRow(int row) => Written is not null && row == Rows.Rows.Count;

    public override string? DeleteTitle(int rows) => Written is null ? null : $"Delete {Plural.Noun(rows, "Row")}";

    /// <summary>A written table's cells, but those the server writes itself: a computed one, a rowversion, an identity.</summary>
    public override bool IsEditable(int row, int column) => Written?.Columns[column] is { ReadOnly: false, Identity: false };

    /// <summary>A row's key, as the parameters a change tells it by.</summary>
    public IEnumerable<KeyValuePair<string, object>> KeyOf(int row, string suffix = "")
    {
        int at = 0;
        for (int column = 0; column < Written!.Columns.Count; column++)
        {
            if (Written.Columns[column].PrimaryKey)
            {
                yield return new($"@k{at++}{suffix}", Rows.Rows[row][column] ?? DBNull.Value);
            }
        }
    }
}
