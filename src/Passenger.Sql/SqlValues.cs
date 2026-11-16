using System.Globalization;

namespace Passenger.Sql;

/// <summary>A value of a row as a cell shows it and as a cell's text goes back to the server: NULL said as the word,
/// a date as ISO writes one, bytes as hexadecimal, a bit as 1 or 0, a number with a point whatever the machine's
/// language, so what is shown can be typed again.</summary>
public static class SqlValues
{
    /// <summary>How a cell says NULL, and what a person types to write one.</summary>
    public const string Null = "NULL";

    /// <summary>The most bytes of a binary value a cell shows.</summary>
    private const int MostBytes = 64;

    public static string Shown(object? value) => value switch
    {
        null or DBNull => Null,
        string text => text,
        bool bit => bit ? "1" : "0",
        DateTime { TimeOfDay.Ticks: 0 } date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime time => time.ToString(time.Millisecond == 0 && time.Microsecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset time => time.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        TimeSpan time => time.ToString("c", CultureInfo.InvariantCulture),
        byte[] bytes => bytes.Length <= MostBytes ? "0x" + Convert.ToHexString(bytes) : $"0x{Convert.ToHexString(bytes, 0, MostBytes)}… ({bytes.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes)",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Whether a value is a number, drawn against its column's right edge.</summary>
    public static bool IsNumber(object? value) => value is byte or short or int or long or decimal or float or double;

    /// <summary>What a cell's text is as a parameter's value: NULL for the word, else the text, which the server turns
    /// into its column's type as it does a literal's.</summary>
    public static object Written(string text) => text == Null ? DBNull.Value : text;
}
