using Microsoft.CodeAnalysis.CSharp;
using Passenger.Core;
using Passenger.Xaml;
using Passenger.Xaml.Features;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Projects;
using Passenger.Xaml.Syntax;

namespace Passenger.UnitTests.Xaml;

/// <summary>A Visual Basic project's XAML reads its own code as a C# project's does: the types its .vb files declare, in
/// its root namespace, with the language's default access, an x:Class written as WPF's Visual Basic templates write it
/// (within the root namespace), and a converter made from usage written in Visual Basic.</summary>
public sealed class VisualBasicXamlTests : IDisposable
{
    private const string Window = """
        <Window x:Class="MainWindow" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:local="clr-namespace:Shop.ViewModels">
            <TextBlock Text="{Binding Title}" />
        </Window>
        """;

    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_visual_basic_projects_types_lie_in_its_root_namespace_with_the_languages_default_access()
    {
        XamlProject project = Shop().Load();

        ClrType model = project.Types.Find("Shop.ViewModels.MainViewModel") ?? throw new InvalidOperationException(
            "The view model's class is not in the root namespace Shop, so its XAML's bindings to it are marked unresolved");
        ClrMember title = model.Members.Single(m => m.Name == "Title");
        Assert.True(title is { Kind: MemberKind.Property, IsPublic: true, CanWrite: true, Type.Name: "System.String" },
            "A property written without its access is not read as the public, writable String it is");
        Assert.False(model.Members.Single(m => m.Name == "Count").CanWrite, "A ReadOnly property is offered as one a binding can write");
        Assert.False(model.Members.Single(m => m.Name == "_title").IsPublic, "A field declared with Dim is offered to XAML, though it is private");
        Assert.True(model.Members.Single(m => m.Name == "Items").Type.Name.StartsWith("ObservableCollection`1", StringComparison.Ordinal),
            "A property declared As New is not read as the type it makes");
        Assert.True(project.Types.Find("Shop.Settings")?.Members.Single(m => m.Name == "Accent").IsStatic == true,
            "A module's member is not shared, so {x:Static} cannot name it");
        Assert.NotNull(project.Types.Find("Elsewhere.Note"));
    }

    [Fact]
    public void An_x_class_written_within_the_root_namespace_names_its_code_behind_and_raises_no_error()
    {
        XamlProjectBuilder builder = Shop();
        string path = Path.Combine(root, "Shop", "MainWindow.xaml");
        XamlProject project = builder.Load();
        XamlFile file = new(path, XmlTree.Parse(Window), project);

        Assert.Equal("Shop.MainWindow", file.ClassType?.FullName);
        Assert.DoesNotContain(XamlDiagnostics.Of(file), d => d.Message.Contains("Cannot resolve class", StringComparison.Ordinal));
        XamlSymbol? symbol = XamlNavigation.At(file, Window.IndexOf("\"MainWindow\"", StringComparison.Ordinal) + 2);
        Assert.True(symbol?.Targets.Single().Place?.Path == Path.Combine(root, "Shop", "MainWindow.xaml.vb"),
            "Go to Declaration on the x:Class does not lead to the Visual Basic code-behind");
        Assert.Contains(file.ClassType!.Members, m => m is { Kind: MemberKind.Method, Name: "Save_Click" });
    }

    [Fact]
    public void A_visual_basic_project_imports_the_sdks_namespaces_and_its_own()
    {
        ProjectInputs inputs = ProjectInputs.Read(Path.Combine(Shop().Folder, "Shop.vbproj"), null);

        Assert.Equal("Shop", inputs.RootNamespace);
        Assert.Contains("System.Collections.ObjectModel", inputs.ImplicitUsings);
        Assert.Contains("System.Linq", inputs.ImplicitUsings);
        Assert.DoesNotContain("System.Xml.Linq", inputs.ImplicitUsings);
        Assert.All(inputs.SourceFiles, f => Assert.EndsWith(".vb", f, StringComparison.Ordinal));
    }

    [Fact]
    public void A_converter_made_from_usage_in_a_visual_basic_project_is_visual_basic_in_the_root_namespace()
    {
        XamlProjectBuilder builder = Shop();
        XamlFile file = new(Path.Combine(root, "Shop", "MainWindow.xaml"), XmlTree.Parse(Window), builder.Load());

        XamlFix fix = CreateFromUsage.Converter(file, "PriceConverter") ?? throw new InvalidOperationException("No converter is offered");
        XamlFileChange made = fix.Others.Single();

        Assert.Equal(Path.Combine(root, "Shop", "PriceConverter.vb"), made.Path);
        Assert.Equal("""
            Imports System.Globalization
            Imports System.Windows.Data

            Public Class PriceConverter
                Implements IValueConverter

                Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
                    Throw New NotImplementedException()
                End Function

                Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
                    Throw New NotImplementedException()
                End Function
            End Class

            """.ReplaceLineEndings("\n"), made.Made);
    }

    /// <summary>A WPF project in Visual Basic, as its template makes it, with a view model, a module and a type outside its
    /// root namespace.</summary>
    private XamlProjectBuilder Shop()
    {
        string folder = Path.Combine(root, "Shop");
        Directory.CreateDirectory(Path.Combine(folder, "ViewModels"));
        File.WriteAllText(Path.Combine(folder, "Shop.vbproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>WinExe</OutputType><UseWPF>true</UseWPF></PropertyGroup>
              <ItemGroup>
                <Import Include="System.Collections.ObjectModel" />
                <Import Remove="System.Xml.Linq" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(folder, "MainWindow.xaml"), Window);
        File.WriteAllText(Path.Combine(folder, "MainWindow.xaml.vb"), """
            Class MainWindow
                Private Sub Save_Click(sender As Object, e As RoutedEventArgs)
                End Sub
            End Class
            """);
        File.WriteAllText(Path.Combine(folder, "ViewModels", "MainViewModel.vb"), """
            Namespace ViewModels
                Public Class MainViewModel
                    Dim _title As String
                    Property Title As String
                        Get
                            Return _title
                        End Get
                        Set(value As String)
                            _title = value
                        End Set
                    End Property
                    Public ReadOnly Property Count As Integer
                    Public Property Items As New ObservableCollection(Of String)
                End Class
            End Namespace
            """);
        File.WriteAllText(Path.Combine(folder, "Settings.vb"), """
            Public Module Settings
                Public Property Accent As String = "Blue"
            End Module

            Namespace Global.Elsewhere
                Public Class Note
                End Class
            End Namespace
            """);
        return XamlProjectBuilder.For(Path.Combine(folder, "Shop.vbproj"), null, CSharpParseOptions.Default)
            ?? throw new InvalidOperationException("The Visual Basic project was not read as WPF's");
    }
}
