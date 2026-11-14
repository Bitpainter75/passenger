using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>Enter in Visual Basic: at the end of a line that opens a block the caret goes a level in, and the block's end is
/// written below where the file has none for it.</summary>
public sealed class VisualBasicBlocksTests
{
    [Theory]
    [InlineData("Module Shop|", "Module Shop\n    |\nEnd Module")]
    [InlineData("Class Shop\n    Public Sub Open()|\nEnd Class", "Class Shop\n    Public Sub Open()\n        |\n    End Sub\nEnd Class")]
    [InlineData("    <Obsolete(\"a > b\")> Private Shared Async Function Total() As Task(Of Integer)|", "    <Obsolete(\"a > b\")> Private Shared Async Function Total() As Task(Of Integer)\n        |\n    End Function")]
    [InlineData("    If rows > 0 Then ' Some.|", "    If rows > 0 Then ' Some.\n        |\n    End If")]
    [InlineData("    if rows > 0 then|", "    if rows > 0 then\n        |\n    End If")]
    [InlineData("    For Each row In rows|", "    For Each row In rows\n        |\n    Next")]
    [InlineData("    Do While open|", "    Do While open\n        |\n    Loop")]
    [InlineData("    Select Case kind|", "    Select Case kind\n        |\n    End Select")]
    [InlineData("    Try|", "    Try\n        |\n    End Try")]
    [InlineData("    Using reader = Open()|", "    Using reader = Open()\n        |\n    End Using")]
    [InlineData("    Dim twice = Function(x As Integer)|", "    Dim twice = Function(x As Integer)\n        |\n    End Function")]
    [InlineData("    AddHandler button.Click, Sub(sender, e)|", "    AddHandler button.Click, Sub(sender, e)\n        |\n    End Sub")]
    [InlineData("#Region \"Sizes\"|", "#Region \"Sizes\"\n    |\n#End Region")]
    public void Enter_after_a_line_that_opens_a_block_indents_its_body_and_writes_its_end(string before, string after) => Assert.Equal(after, Entered(before));

    [Theory]
    [InlineData("    Sub Open()|\n    End Sub", "    Sub Open()\n        |\n    End Sub")]
    [InlineData("If a Then\n    If b Then|\nEnd If", "If a Then\n    If b Then\n        |\n    End If\nEnd If")]
    [InlineData("Sub A()|\nEnd Sub\nSub B()\nEnd Sub", "Sub A()\n    |\nEnd Sub\nSub B()\nEnd Sub")]
    [InlineData("Dim f = Sub()\n        End Sub\nSub A()|", "Dim f = Sub()\n        End Sub\nSub A()\n    |\nEnd Sub")]
    public void A_block_that_has_its_end_gets_no_second_one_and_one_typed_inside_another_of_its_kind_gets_its_own(string before, string after) =>
        Assert.Equal(after, Entered(before));

    [Theory]
    [InlineData("    If rows > 0 Then Return|", "    If rows > 0 Then Return\n    |")]
    [InlineData("    Public Property Name As String|", "    Public Property Name As String\n    |")]
    [InlineData("    Public MustOverride Sub Open()|", "    Public MustOverride Sub Open()\n    |")]
    [InlineData("Interface IShop\n    Sub Open()|\nEnd Interface", "Interface IShop\n    Sub Open()\n    |\nEnd Interface")]
    [InlineData("    Dim twice = Function(x) x * 2|", "    Dim twice = Function(x) x * 2\n    |")]
    [InlineData("    Declare Sub Beep Lib \"kernel32\" ()|", "    Declare Sub Beep Lib \"kernel32\" ()\n    |")]
    [InlineData("    Exit Sub|", "    Exit Sub\n    |")]
    [InlineData("    ' If rows > 0 Then|", "    ' If rows > 0 Then\n    |")]
    [InlineData("    Sub Op|en()", "    Sub Op\n    |en()")]
    public void Enter_after_a_line_that_opens_no_block_or_inside_one_only_carries_the_indentation(string before, string after) => Assert.Equal(after, Entered(before));

    private static string Entered(string marked)
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(marked);
        return Marked.After(text, TextEditing.NewLine(text, selections, "\n", new IndentOptions(), blocks: VisualBasicBlocks.Opened)) ?? string.Empty;
    }
}
