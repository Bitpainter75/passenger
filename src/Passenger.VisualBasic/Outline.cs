using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Passenger.VisualBasic;

/// <summary>A file's declarations as the protocol's document symbols, each holding those declared inside it: namespaces,
/// types, their members and an enum's values, read from the file's syntax alone.</summary>
internal static class Outline
{
    public static async Task<JsonNode?> OfAsync(Document document, SourceText text, CancellationToken cancel)
    {
        JsonArray symbols = [];
        if (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is { } root)
        {
            Fill(symbols, root, text);
        }
        return symbols;
    }

    private static void Fill(JsonArray into, SyntaxNode parent, SourceText text)
    {
        foreach (SyntaxNode node in parent.ChildNodes())
        {
            (SyntaxToken Name, int Kind)? declared = node switch
            {
                NamespaceBlockSyntax space => (space.NamespaceStatement.Name.GetLastToken(), 3),
                ModuleBlockSyntax module => (module.BlockStatement.Identifier, 2),
                InterfaceBlockSyntax contract => (contract.BlockStatement.Identifier, 11),
                StructureBlockSyntax structure => (structure.BlockStatement.Identifier, 23),
                ClassBlockSyntax type => (type.BlockStatement.Identifier, 5),
                EnumBlockSyntax values => (values.EnumStatement.Identifier, 10),
                EnumMemberDeclarationSyntax value => (value.Identifier, 22),
                DelegateStatementSyntax signature => (signature.Identifier, 5),
                ConstructorBlockSyntax made => (made.SubNewStatement.NewKeyword, 9),
                MethodBlockSyntax method => (method.SubOrFunctionStatement.Identifier, 6),
                MethodStatementSyntax { Parent: not MethodBlockBaseSyntax } method => (method.Identifier, 6),
                OperatorBlockSyntax made => (made.OperatorStatement.OperatorToken, 25),
                PropertyBlockSyntax property => (property.PropertyStatement.Identifier, 7),
                PropertyStatementSyntax { Parent: not PropertyBlockSyntax } property => (property.Identifier, 7),
                EventBlockSyntax raised => (raised.EventStatement.Identifier, 24),
                EventStatementSyntax { Parent: not EventBlockSyntax } raised => (raised.Identifier, 24),
                _ => null,
            };
            if (declared is var (name, kind))
            {
                JsonArray children = [];
                Fill(children, node, text);
                into.Add(Symbol(name.ValueText, kind, node, name, text, children));
            }
            else if (node is FieldDeclarationSyntax field)
            {
                bool constant = field.Modifiers.Any(SyntaxKind.ConstKeyword);
                foreach (ModifiedIdentifierSyntax variable in field.Declarators.SelectMany(d => d.Names))
                {
                    into.Add(Symbol(variable.Identifier.ValueText, constant ? 14 : 8, field, variable.Identifier, text, []));
                }
            }
        }
    }

    private static JsonObject Symbol(string name, int kind, SyntaxNode node, SyntaxToken named, SourceText text, JsonArray children) => new()
    {
        ["name"] = name.Length > 0 ? name : "?",
        ["kind"] = kind,
        ["range"] = Places.Range(text, node.Span),
        ["selectionRange"] = Places.Range(text, named.Span),
        ["children"] = children,
    };
}
