using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;

namespace Passenger.VisualBasic;

/// <summary>A file's colours as the compiler knows them, the protocol's semantic tokens: what only binding tells (that a
/// name is a structure, a field, a parameter, an extension method, a keyword only where it stands) from Roslyn's
/// classifier, each token on one line and none over another.</summary>
internal static class Colours
{
    /// <summary>The kinds of token, by the names Passenger's colours know them by.</summary>
    private static readonly string[] Types =
    [
        "namespace", "class", "struct", "interface", "enum", "delegate", "typeParameter", "method", "property", "event", "field", "constant",
        "enumMember", "variable", "parameter", "label", "keyword", "controlKeyword", "string", "stringEscapeCharacter", "number", "comment",
        "excludedCode", "preprocessorKeyword", "xmlDocCommentText",
    ];

    public static JsonObject Provider() => new()
    {
        ["legend"] = new JsonObject { ["tokenTypes"] = new JsonArray([.. Types.Select(t => (JsonNode)t)]), ["tokenModifiers"] = new JsonArray() },
        ["full"] = true,
        ["range"] = true,
    };

    public static async Task<JsonNode?> OfAsync(Document document, SourceText text, TextSpan span, CancellationToken cancel)
    {
        JsonArray data = [];
        int line = 0;
        int column = 0;
        int end = 0;
        foreach (ClassifiedSpan classified in (await Classifier.GetClassifiedSpansAsync(document, span, cancel).ConfigureAwait(false)).OrderBy(c => c.TextSpan.Start))
        {
            // What says more of a name it already named (a static symbol) starts where that one did.
            if (Type(classified.ClassificationType) is not { } type || classified.TextSpan.Start < end)
            {
                continue;
            }
            end = classified.TextSpan.End;
            LinePositionSpan lines = text.Lines.GetLinePositionSpan(classified.TextSpan);
            for (int at = lines.Start.Line; at <= lines.End.Line; at++)
            {
                int from = at == lines.Start.Line ? lines.Start.Character : 0;
                int to = at == lines.End.Line ? lines.End.Character : text.Lines[at].End - text.Lines[at].Start;
                if (to <= from)
                {
                    continue;
                }
                data.Add(at - line);
                data.Add(at == line ? from - column : from);
                data.Add(to - from);
                data.Add(type);
                data.Add(0);
                (line, column) = (at, from);
            }
        }
        return new JsonObject { ["data"] = data };
    }

    private static int? Type(string classification)
    {
        string? named = classification switch
        {
            ClassificationTypeNames.NamespaceName => "namespace",
            ClassificationTypeNames.ClassName or ClassificationTypeNames.ModuleName or ClassificationTypeNames.RecordClassName => "class",
            ClassificationTypeNames.StructName or ClassificationTypeNames.RecordStructName => "struct",
            ClassificationTypeNames.InterfaceName => "interface",
            ClassificationTypeNames.EnumName => "enum",
            ClassificationTypeNames.DelegateName => "delegate",
            ClassificationTypeNames.TypeParameterName => "typeParameter",
            ClassificationTypeNames.MethodName or ClassificationTypeNames.ExtensionMethodName => "method",
            ClassificationTypeNames.PropertyName => "property",
            ClassificationTypeNames.EventName => "event",
            ClassificationTypeNames.FieldName => "field",
            ClassificationTypeNames.ConstantName => "constant",
            ClassificationTypeNames.EnumMemberName => "enumMember",
            ClassificationTypeNames.LocalName => "variable",
            ClassificationTypeNames.ParameterName => "parameter",
            ClassificationTypeNames.LabelName => "label",
            ClassificationTypeNames.Keyword => "keyword",
            ClassificationTypeNames.ControlKeyword => "controlKeyword",
            ClassificationTypeNames.StringLiteral or ClassificationTypeNames.VerbatimStringLiteral => "string",
            ClassificationTypeNames.StringEscapeCharacter => "stringEscapeCharacter",
            ClassificationTypeNames.NumericLiteral => "number",
            ClassificationTypeNames.Comment => "comment",
            ClassificationTypeNames.ExcludedCode => "excludedCode",
            ClassificationTypeNames.PreprocessorKeyword => "preprocessorKeyword",
            _ => classification.StartsWith("xml doc comment", StringComparison.Ordinal) ? "xmlDocCommentText" : null,
        };
        return named is null ? null : Array.IndexOf(Types, named);
    }
}
