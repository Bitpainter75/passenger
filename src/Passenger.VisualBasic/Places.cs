using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Passenger.VisualBasic;

/// <summary>Places as the protocol says them (a file's URI, a zero-based line and a character in UTF-16 units) and as
/// Roslyn does (a path, an offset, a span), each way. A place past a line's or the text's end is taken as that end.</summary>
internal static class Places
{
    public static string PathOf(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) && parsed.IsFile ? parsed.LocalPath : uri;

    public static string UriOf(string path) => new Uri(path).AbsoluteUri;

    /// <summary>The path a request's or notification's <c>textDocument</c> names, or null.</summary>
    public static string? DocumentOf(JsonNode? parameters) => (string?)parameters?["textDocument"]?["uri"] is { } uri ? PathOf(uri) : null;

    public static int Offset(SourceText text, JsonNode? position)
    {
        int line = Math.Clamp((int?)position?["line"] ?? 0, 0, text.Lines.Count - 1);
        TextLine at = text.Lines[line];
        return at.Start + Math.Clamp((int?)position?["character"] ?? 0, 0, at.End - at.Start);
    }

    public static TextSpan Span(SourceText text, JsonNode? range)
    {
        int start = Offset(text, range?["start"]);
        return TextSpan.FromBounds(start, Math.Max(start, Offset(text, range?["end"])));
    }

    public static JsonObject Position(SourceText text, int offset)
    {
        LinePosition at = text.Lines.GetLinePosition(Math.Clamp(offset, 0, text.Length));
        return new JsonObject { ["line"] = at.Line, ["character"] = at.Character };
    }

    public static JsonObject Range(SourceText text, TextSpan span) =>
        new() { ["start"] = Position(text, span.Start), ["end"] = Position(text, span.End) };

    /// <summary>A place in source as the protocol's Location, or null for one in no file.</summary>
    public static JsonObject? Location(Location location)
    {
        if (!location.IsInSource || location.SourceTree is not { FilePath.Length: > 0 } tree)
        {
            return null;
        }
        return new JsonObject { ["uri"] = UriOf(tree.FilePath), ["range"] = Range(tree.GetText(), location.SourceSpan) };
    }
}
