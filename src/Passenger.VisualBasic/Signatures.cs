using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Passenger.VisualBasic;

/// <summary>The signatures of what a call's arguments are being written for, read from the semantic model (Roslyn's own
/// signature help is not public): a method's overloads, a type's constructors for <c>New</c> and for an attribute, an
/// indexed property and a delegate's call, with the overload the compiler bound first, else the first that takes as many
/// arguments, and the parameter the caret is at by the commas before it.</summary>
internal static class Signatures
{
    public static async Task<JsonNode?> OfAsync(Document document, int at, CancellationToken cancel)
    {
        if (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root
            || await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
        {
            return null;
        }
        ArgumentListSyntax? list = root.FindToken(Math.Max(0, at - 1)).Parent?.AncestorsAndSelf().OfType<ArgumentListSyntax>()
            .FirstOrDefault(l => l.OpenParenToken.Span.End <= at && (l.CloseParenToken.IsMissing || at <= l.CloseParenToken.SpanStart));
        if (list?.Parent is not { } call)
        {
            return null;
        }
        ImmutableArray<ISymbol> candidates = Candidates(model, call, cancel);
        if (candidates.IsEmpty)
        {
            return null;
        }
        int parameter = list.Arguments.GetSeparators().Count(separator => separator.Span.End <= at);
        ISymbol? bound = model.GetSymbolInfo(call, cancel).Symbol?.OriginalDefinition;
        int active = candidates.IndexOf(candidates.FirstOrDefault(c => SymbolEqualityComparer.Default.Equals(c.OriginalDefinition, bound))
            ?? candidates.FirstOrDefault(c => Takes(c, parameter)) ?? candidates[0]);
        JsonArray signatures = [];
        foreach (ISymbol candidate in candidates)
        {
            XElement? documented = Documented(candidate, cancel);
            JsonArray parameters = [];
            foreach (IParameterSymbol taken in Parameters(candidate))
            {
                JsonObject said = new() { ["label"] = taken.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) };
                if (documented?.Elements("param").FirstOrDefault(p => (string?)p.Attribute("name") == taken.Name) is { } about && Words(about) is { Length: > 0 } words)
                {
                    said["documentation"] = words;
                }
                parameters.Add(said);
            }
            JsonObject signature = new() { ["label"] = candidate.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), ["parameters"] = parameters };
            if (documented?.Element("summary") is { } summary && Words(summary) is { Length: > 0 } summed)
            {
                signature["documentation"] = summed;
            }
            signatures.Add(signature);
        }
        ImmutableArray<IParameterSymbol> chosen = Parameters(candidates[active]);
        return new JsonObject
        {
            ["signatures"] = signatures,
            ["activeSignature"] = active,
            // A ParamArray takes every argument past the last parameter.
            ["activeParameter"] = chosen.IsEmpty ? 0 : Math.Min(parameter, chosen.Length - 1),
        };
    }

    private static ImmutableArray<ISymbol> Candidates(SemanticModel model, SyntaxNode call, CancellationToken cancel)
    {
        switch (call)
        {
            case InvocationExpressionSyntax { Expression: { } called }:
                ImmutableArray<ISymbol> group = model.GetMemberGroup(called, cancel);
                if (!group.IsEmpty)
                {
                    return group;
                }
                ISymbol? named = model.GetSymbolInfo(called, cancel).Symbol;
                if (named is IPropertySymbol { Parameters.Length: > 0 })
                {
                    return [named];
                }
                return model.GetTypeInfo(called, cancel).Type is INamedTypeSymbol { DelegateInvokeMethod: { } invoked } ? [invoked] : [];
            case ObjectCreationExpressionSyntax made:
                return Constructors(model.GetSymbolInfo(made.Type, cancel).Symbol as INamedTypeSymbol);
            case AttributeSyntax attribute:
                SymbolInfo found = model.GetSymbolInfo(attribute, cancel);
                return Constructors((found.Symbol ?? found.CandidateSymbols.FirstOrDefault())?.ContainingType);
            default:
                return [];
        }
    }

    private static ImmutableArray<ISymbol> Constructors(INamedTypeSymbol? type) =>
        type is null ? [] : [.. type.InstanceConstructors.Where(c => c.DeclaredAccessibility != Accessibility.Private)];

    private static ImmutableArray<IParameterSymbol> Parameters(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.Parameters,
        IPropertySymbol property => property.Parameters,
        _ => [],
    };

    private static bool Takes(ISymbol symbol, int parameter) =>
        Parameters(symbol) is { } taken && (parameter < taken.Length || (taken.Length > 0 && taken[^1].IsParams));

    /// <summary>A symbol's documentation comment as XML, or null where it has none or it does not read.</summary>
    private static XElement? Documented(ISymbol symbol, CancellationToken cancel)
    {
        string? xml = symbol.GetDocumentationCommentXml(cancellationToken: cancel);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }
        try
        {
            return XElement.Parse(xml.TrimStart().StartsWith("<member", StringComparison.Ordinal) ? xml : $"<doc>{xml}</doc>");
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>A documentation element's words on one line, a reference said by its name.</summary>
    private static string Words(XElement element)
    {
        IEnumerable<string> parts = element.Nodes().Select(node => node switch
        {
            XText text => text.Value,
            XElement inner when (string?)inner.Attribute("cref") is { } named => named[(named.LastIndexOf('.') + 1)..],
            XElement inner when (string?)inner.Attribute("name") is { } named => named,
            XElement inner when (string?)inner.Attribute("langword") is { } word => word,
            XElement inner => inner.Value,
            _ => string.Empty,
        });
        return string.Join(' ', string.Concat(parts).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
