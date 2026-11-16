using System.Globalization;

namespace Passenger.Sql;

/// <summary>A column's type as a script writes it, from what the catalog keeps of it: its name with the length, or the
/// precision and scale, its kind takes, a length in characters where the catalog counts bytes, "max" for -1.</summary>
public static class SqlTypes
{
    /// <summary>The types a designer offers first; any other is typed.</summary>
    public static IReadOnlyList<string> Common { get; } =
    [
        "int", "bigint", "smallint", "tinyint", "bit", "decimal(18, 2)", "money", "float", "nvarchar(50)", "nvarchar(max)", "varchar(50)", "varchar(max)",
        "nchar(10)", "char(10)", "date", "datetime2", "datetimeoffset", "time", "uniqueidentifier", "varbinary(max)", "xml",
    ];

    public static string Written(string type, int length, int precision, int scale)
    {
        string number(int value) => value.ToString(CultureInfo.InvariantCulture);
        return type.ToLowerInvariant() switch
        {
            "nvarchar" or "nchar" => $"{type}({(length < 0 ? "max" : number(length / 2))})",
            "varchar" or "char" or "varbinary" or "binary" => $"{type}({(length < 0 ? "max" : number(length))})",
            "decimal" or "numeric" => $"{type}({number(precision)}, {number(scale)})",
            "datetime2" or "time" or "datetimeoffset" when scale != 7 => $"{type}({number(scale)})",
            _ => type,
        };
    }

    /// <summary>Whether the server keeps a type's values itself, so no row writes one.</summary>
    public static bool ServerWritten(string type) => type.ToLowerInvariant() is "timestamp" or "rowversion";
}
