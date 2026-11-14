using Passenger.Debugger.ExpressionEvaluator.Compiler;

namespace Passenger.UnitTests.Debugger;

/// <summary>A Visual Basic expression read for the debugger: its readings in C#, calls before indexes, and none for what is
/// no Visual Basic or has no words in C#.</summary>
public sealed class VisualBasicExpressionsTests
{
    [Theory]
    [InlineData("total", "total")]
    [InlineData("Me.Name", "this.Name")]
    [InlineData("MyBase.ToString()", "base.ToString()")]
    [InlineData("label Is Nothing", "label == null")]
    [InlineData("label IsNot Nothing", "label != null")]
    [InlineData("a Is b", "object.ReferenceEquals(a, b)")]
    [InlineData("open AndAlso total > 3 OrElse Not closed", "open && total > 3 || !(closed)")]
    [InlineData("total = 6", "total == 6")]
    [InlineData("total <> 6", "total != 6")]
    [InlineData("first & \" \" & last", "string.Concat(string.Concat(first, \" \"), last)")]
    [InlineData("2 ^ 8", "System.Math.Pow(2, 8)")]
    [InlineData("10 Mod 3 + 7 \\ 2", "10 % 3 + 7 / 2")]
    [InlineData("If(label, \"none\")", "((label) ?? (\"none\"))")]
    [InlineData("If(open, 1, 2)", "((open) ? (1) : (2))")]
    [InlineData("CType(total, Long)", "((long)(total))")]
    [InlineData("DirectCast(shape, Circle).Radius", "((Circle)(shape)).Radius")]
    [InlineData("TryCast(shape, Circle)", "((shape) as Circle)")]
    [InlineData("CInt(2.5)", "System.Convert.ToInt32(2.5D)")]
    [InlineData("TypeOf names Is IEnumerable(Of String)", "((names) is IEnumerable<string>)")]
    [InlineData("GetType(Dictionary(Of String, Integer()))", "typeof(Dictionary<string, int[]>)")]
    [InlineData("New List(Of Integer) From {1, 2}", "new List<int>() { 1, 2 }")]
    [InlineData("New Point With {.X = 1, .Y = 2}", "new Point() { X = 1, Y = 2 }")]
    [InlineData("{1, 2, 3}", "new[] { 1, 2, 3 }")]
    [InlineData("\"say \"\"hi\"\"\"", "\"say \\\"hi\\\"\"")]
    [InlineData("\"a\"c", "'a'")]
    [InlineData("5L + 2.5F + 1.5R + 3D", "5L + 2.5F + 1.5D + 3M")]
    [InlineData("$\"{Name}: {total,5:D3} {{x}}\"", "$\"{(Name)}: {(total),5:D3} {{x}}\"")]
    [InlineData("shop?.Name", "shop?.Name")]
    [InlineData("[class].event", "@class.@event")]
    [InlineData("Twice()", "Twice()")]
    public void A_Visual_Basic_expression_reads_in_CSharp(string written, string read) => Assert.Equal(read, VisualBasicExpressions.ToCSharp(written)[0]);

    [Fact]
    public void A_name_with_arguments_reads_as_a_call_first_then_as_an_index_each_of_them_in_turn()
    {
        Assert.Equal(["rows(1)", "rows[1]"], VisualBasicExpressions.ToCSharp("rows(1)"));
        Assert.Equal(["Twice(rows(2))", "Twice[rows(2)]", "Twice(rows[2])", "Twice[rows[2]]"], VisualBasicExpressions.ToCSharp("Twice(rows(2))"));
        Assert.Equal(["names.Item(0, key: 1)", "names.Item[0, key: 1]"], VisualBasicExpressions.ToCSharp("names.Item(0, key:=1)"));
    }

    [Theory]
    [InlineData("rows[1]")]
    [InlineData("total == 6")]
    [InlineData("open && closed")]
    [InlineData("a Like \"b*\"")]
    [InlineData("#1/31/2026#")]
    [InlineData("Function(x) x + 1")]
    [InlineData("From row In rows Select row")]
    [InlineData("AddressOf Twice")]
    [InlineData("total +")]
    [InlineData("")]
    public void What_is_no_Visual_Basic_expression_or_has_no_words_in_CSharp_has_no_reading(string written) => Assert.Empty(VisualBasicExpressions.ToCSharp(written));
}
