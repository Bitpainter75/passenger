using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Visual Basic through the window, on a console project of two files with Passenger's own language server for
/// it (src/Passenger.VisualBasic), which the project host's design-time builds feed: what the compiler says of a file, its
/// completion, Go to Declaration, Rename and Reformat Code, and the types Class/Interface makes in such a project. It needs
/// no Node.js and no C# language server.</summary>
public sealed class VisualBasicTests : IClassFixture<VisualBasicTests.Project>
{
    private readonly Project project;

    public VisualBasicTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    [AvaloniaFact]
    public void A_Visual_Basic_file_marks_the_compilers_error_completes_a_member_shows_its_signature_goes_to_a_declaration_renames_a_local_and_reformats()
    {
        MainWindow window = project.Open("Program.vb", languageServer: false);
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        // One error alone: the other file's module and the project's imports are known, so the file is read in its project.
        Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Error), "the error under the member String has not");
        Marker error = Assert.Single(document.Markers, m => m.Kind == MarkerKind.Error);
        Assert.Contains("'Lenght' is not a member of 'String'", error.Message, StringComparison.Ordinal);
        Assert.Equal(13, editor.Document.Snapshot.GetLineFromOffset(error.Range.Start));

        Ui.PlaceAfter(editor, "Console.WriteLine(Total({1, 2, 3}) + Orders.Count())");
        Ui.Press(window, "ENTER");
        foreach (char typed in "Console")
        {
            window.KeyTextInput(typed.ToString());
        }
        window.KeyTextInput(".");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("WriteLine"), "Console's members after its dot");
        window.KeyTextInput("WriteL");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "WriteLine", "the list narrowed to WriteLine");
        Ui.Press(window, "ENTER");
        Assert.Equal("Console.WriteLine", editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line).Trim());
        window.KeyTextInput("(");
        Ui.WaitFor(() => window.Assist.SignatureText?.Contains("Console.WriteLine(", StringComparison.Ordinal) == true, "WriteLine's signature after its bracket");
        window.KeyTextInput(")");

        Ui.PlaceAfter(editor, "Console.WriteLine(Tot");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.WaitFor(() => editor.CaretPosition.Line == 4, "the caret at the function's declaration");
        Assert.Equal(new TextPosition(4, 13), editor.CaretPosition);

        Ui.PlaceAfter(editor, "Dim su");
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "sum", "the rename box on the local's name");
        window.KeyTextInput("total");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("Return total", StringComparison.Ordinal), "the local's uses renamed with it");
        Assert.Contains("total += row", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Contains("Dim total As Integer = 0", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.ToString().IndexOf("        Return total", StringComparison.Ordinal), 8, "  ")]);
        Ui.Run(window, AppActions.Reformat);
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("\n        Return total\n", StringComparison.Ordinal),
            "the statement indented again by Visual Basic's formatter");
        window.Close();
    }

    [AvaloniaFact]
    public void Class_Interface_in_a_Visual_Basic_project_makes_a_Visual_Basic_type_and_New_Item_offers_Visual_Basics_kinds()
    {
        MainWindow window = project.Open("Program.vb", languageServer: false);
        string made = project.PathOf("Basket.vb");
        try
        {
            Assert.Null(window.CreateType(project.Folder, "Basket", "Module"));
            Assert.Equal("Public Module Basket\n\nEnd Module\n", File.ReadAllText(made));
            Assert.Equal(made, window.EditorArea.Active?.Path);
            Assert.Equal("1 is not a Visual Basic name", window.CreateType(project.Folder, "1", "Class"));
            Assert.Equal(["Class", "Module", "Interface", "Structure", "Enum"],
                Passenger.App.Templates.OwnTemplates.All.Where(t => t.Variants.Any(v => v.Language == "VB")).Select(t => t.Name));
            Assert.All(Passenger.App.Templates.OwnTemplates.All.Where(t => t.Variants.Any(v => v.Language == "VB")),
                t => Assert.True(t.Own is { Extension: ".vb", Namespaced: false }, $"{t.Name} is no Visual Basic file of its project's root namespace"));
        }
        finally
        {
            window.Close();
            File.Delete(made);
        }
    }

    /// <summary>A Visual Basic console project of two files, restored once for the class's tests.</summary>
    public sealed class Project : SharedProject
    {
        public Project()
            : base("visualbasic-")
        {
            File.WriteAllText(PathOf("Shop.vbproj"), TestProjects.Console("<RootNamespace>Shop</RootNamespace>"));
            File.WriteAllText(PathOf("Orders.vb"), "Module Orders\n    Function Count() As Integer\n        Return 3\n    End Function\nEnd Module\n");
            File.WriteAllText(PathOf("Program.vb"), "Imports System\n\nModule Program\n    ''' <summary>Sums the orders.</summary>\n"
                + "    Function Total(rows As Integer()) As Integer\n        Dim sum As Integer = 0\n        For Each row In rows\n            sum += row\n        Next\n"
                + "        Return sum\n    End Function\n\n    Sub Main(args As String())\n        Dim count As Integer = \"abc\".Lenght\n"
                + "        Console.WriteLine(Total({1, 2, 3}) + Orders.Count())\n    End Sub\nEnd Module\n");
        }
    }
}
