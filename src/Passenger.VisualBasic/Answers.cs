using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;

namespace Passenger.VisualBasic;

/// <summary>What the server answers, each request from the solution it was asked of, through Roslyn's public services:
/// completion and its descriptions, hover, declarations, usages and their highlights, rename, a file's symbols, Reformat
/// Code and a file's diagnostics. A request the server does not know is answered with nothing.</summary>
internal sealed class Answers
{
    /// <summary>The last completion's document and items, which a resolve of one of them reads its description from.</summary>
    private (Document Document, ImmutableArray<CompletionItem> Items)? completed;

    public static JsonObject Initialized() => new()
    {
        ["capabilities"] = new JsonObject
        {
            ["positionEncoding"] = "utf-16",
            ["textDocumentSync"] = new JsonObject { ["openClose"] = true, ["change"] = 2 },
            ["completionProvider"] = new JsonObject { ["resolveProvider"] = true, ["triggerCharacters"] = new JsonArray(".", " ", "(") },
            ["hoverProvider"] = true,
            ["definitionProvider"] = true,
            ["referencesProvider"] = true,
            ["documentHighlightProvider"] = true,
            ["documentSymbolProvider"] = true,
            ["documentFormattingProvider"] = true,
            ["documentRangeFormattingProvider"] = true,
            ["renameProvider"] = new JsonObject { ["prepareProvider"] = true },
        },
        ["serverInfo"] = new JsonObject { ["name"] = "Passenger Visual Basic", ["version"] = Product.Version },
    };

    public async Task<JsonNode?> AnswerAsync(string method, JsonNode? parameters, Solution solution, CancellationToken cancel)
    {
        if (method == "completionItem/resolve")
        {
            return await ResolvedAsync(parameters, cancel).ConfigureAwait(false);
        }
        if (Places.DocumentOf(parameters) is not { } path || solution.GetDocumentIdsWithFilePath(path) is not [DocumentId id, ..]
            || solution.GetDocument(id) is not { } document)
        {
            return null;
        }
        SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
        int at = Places.Offset(text, parameters?["position"]);
        return method switch
        {
            "textDocument/completion" => await CompletionsAsync(document, text, at, parameters, cancel).ConfigureAwait(false),
            "textDocument/hover" => await HoverAsync(document, text, at, cancel).ConfigureAwait(false),
            "textDocument/definition" => await DeclarationsAsync(document, at, cancel).ConfigureAwait(false),
            "textDocument/references" => await UsagesAsync(document, at, (bool?)parameters?["context"]?["includeDeclaration"] ?? true, null, cancel).ConfigureAwait(false),
            "textDocument/documentHighlight" => await UsagesAsync(document, at, true, text, cancel).ConfigureAwait(false),
            "textDocument/prepareRename" => await RenamableAsync(document, text, at, cancel).ConfigureAwait(false),
            "textDocument/rename" => await RenamedAsync(document, at, (string?)parameters?["newName"] ?? string.Empty, cancel).ConfigureAwait(false),
            "textDocument/documentSymbol" => await Outline.OfAsync(document, text, cancel).ConfigureAwait(false),
            "textDocument/formatting" => await FormattedAsync(document, text, null, parameters?["options"], cancel).ConfigureAwait(false),
            "textDocument/rangeFormatting" => await FormattedAsync(document, text, Places.Span(text, parameters?["range"]), parameters?["options"], cancel).ConfigureAwait(false),
            _ => null,
        };
    }

    /// <summary>A file's errors and warnings, the compiler's hidden ones left out.</summary>
    public static async Task<JsonArray> DiagnosticsAsync(Document document, CancellationToken cancel)
    {
        JsonArray found = [];
        if (await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
        {
            return found;
        }
        SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
        foreach (Diagnostic diagnostic in model.GetDiagnostics(cancellationToken: cancel))
        {
            if (diagnostic.Severity == DiagnosticSeverity.Hidden || !diagnostic.Location.IsInSource)
            {
                continue;
            }
            JsonObject said = new()
            {
                ["range"] = Places.Range(text, diagnostic.Location.SourceSpan),
                ["severity"] = diagnostic.Severity switch { DiagnosticSeverity.Error => 1, DiagnosticSeverity.Warning => 2, _ => 3 },
                ["code"] = diagnostic.Id,
                ["source"] = "vb",
                ["message"] = diagnostic.GetMessage(),
            };
            if (diagnostic.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.Unnecessary))
            {
                said["tags"] = new JsonArray(1);
            }
            found.Add(said);
        }
        return found;
    }

    private async Task<JsonNode?> CompletionsAsync(Document document, SourceText text, int at, JsonNode? parameters, CancellationToken cancel)
    {
        if (CompletionService.GetService(document) is not { } service)
        {
            return null;
        }
        CompletionTrigger trigger = (int?)parameters?["context"]?["triggerKind"] == 2 && (string?)parameters?["context"]?["triggerCharacter"] is [char typed]
            ? CompletionTrigger.CreateInsertionTrigger(typed)
            : CompletionTrigger.Invoke;
        CompletionList list = await service.GetCompletionsAsync(document, at, trigger, cancellationToken: cancel).ConfigureAwait(false);
        completed = (document, list.ItemsList.ToImmutableArray());
        JsonArray items = [];
        for (int i = 0; i < list.ItemsList.Count; i++)
        {
            CompletionItem item = list.ItemsList[i];
            string written = item.Properties.TryGetValue("InsertionText", out string? insertion) ? insertion : item.DisplayText;
            TextSpan span = item.Span;
            if (item.IsComplexTextEdit)
            {
                // One whose text is more than its name (an override's whole declaration): Roslyn's own change, asked now.
                CompletionChange change = await service.GetChangeAsync(document, item, cancellationToken: cancel).ConfigureAwait(false);
                written = change.TextChange.NewText ?? written;
                span = change.TextChange.Span;
            }
            items.Add(new JsonObject
            {
                ["label"] = item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix,
                ["kind"] = Kind(item.Tags),
                ["sortText"] = item.SortText,
                ["filterText"] = item.FilterText,
                ["textEdit"] = new JsonObject { ["range"] = Places.Range(text, span), ["newText"] = written },
                ["data"] = i,
            });
        }
        return new JsonObject { ["isIncomplete"] = false, ["items"] = items };
    }

    private async Task<JsonNode?> ResolvedAsync(JsonNode? item, CancellationToken cancel)
    {
        if (item is not JsonObject resolved || completed is not var (document, items) || (int?)resolved["data"] is not { } index || index < 0 || index >= items.Length
            || CompletionService.GetService(document) is not { } service)
        {
            return item?.DeepClone();
        }
        CompletionDescription? description = await service.GetDescriptionAsync(document, items[index], cancel).ConfigureAwait(false);
        JsonObject answer = (JsonObject)resolved.DeepClone();
        if (description is { Text.Length: > 0 })
        {
            int line = description.Text.IndexOf('\n', StringComparison.Ordinal);
            answer["detail"] = (line < 0 ? description.Text : description.Text[..line]).Trim();
            if (line >= 0 && description.Text[(line + 1)..].Trim() is { Length: > 0 } documentation)
            {
                answer["documentation"] = documentation;
            }
        }
        return answer;
    }

    private static int Kind(ImmutableArray<string> tags)
    {
        foreach (string tag in tags)
        {
            int kind = tag switch
            {
                WellKnownTags.Method or WellKnownTags.ExtensionMethod => 2,
                WellKnownTags.Field => 5,
                WellKnownTags.Local or WellKnownTags.Parameter or WellKnownTags.RangeVariable => 6,
                WellKnownTags.Class or WellKnownTags.Delegate => 7,
                WellKnownTags.Interface => 8,
                WellKnownTags.Module or WellKnownTags.Namespace => 9,
                WellKnownTags.Property => 10,
                WellKnownTags.Enum => 13,
                WellKnownTags.Keyword => 14,
                WellKnownTags.Snippet => 15,
                WellKnownTags.EnumMember => 20,
                WellKnownTags.Constant => 21,
                WellKnownTags.Structure => 22,
                WellKnownTags.Event => 23,
                WellKnownTags.Operator => 24,
                WellKnownTags.TypeParameter => 25,
                _ => 0,
            };
            if (kind != 0)
            {
                return kind;
            }
        }
        return 1;
    }

    private static async Task<JsonNode?> HoverAsync(Document document, SourceText text, int at, CancellationToken cancel)
    {
        if (QuickInfoService.GetService(document) is not { } service || await service.GetQuickInfoAsync(document, at, cancel).ConfigureAwait(false) is not { } info)
        {
            return null;
        }
        StringBuilder said = new();
        foreach (QuickInfoSection section in info.Sections)
        {
            if (section.Text.Length == 0)
            {
                continue;
            }
            said.Append(said.Length > 0 ? "\n\n" : string.Empty);
            said.Append(section.Kind == QuickInfoSectionKinds.Description ? $"```vb\n{section.Text}\n```" : section.Text);
        }
        return said.Length == 0 ? null : new JsonObject
        {
            ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = said.ToString() },
            ["range"] = Places.Range(text, info.Span),
        };
    }

    /// <summary>The symbol at a place as its source declares it, or null: nothing there, or nothing a name stands for.</summary>
    private static async Task<ISymbol?> SymbolAsync(Document document, int at, CancellationToken cancel)
    {
        ISymbol? symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, at, cancel).ConfigureAwait(false);
        if (symbol is null)
        {
            return null;
        }
        return await SymbolFinder.FindSourceDefinitionAsync(symbol, document.Project.Solution, cancel).ConfigureAwait(false) ?? symbol;
    }

    private static async Task<JsonNode?> DeclarationsAsync(Document document, int at, CancellationToken cancel)
    {
        JsonArray found = [];
        if (await SymbolAsync(document, at, cancel).ConfigureAwait(false) is { } symbol)
        {
            foreach (Location location in symbol.Locations)
            {
                if (Places.Location(location) is { } place)
                {
                    found.Add(place);
                }
            }
        }
        return found;
    }

    /// <summary>A symbol's usages across the solution as Locations, or, given <paramref name="within"/> (the asked file's
    /// text), those in that file as highlights.</summary>
    private static async Task<JsonNode?> UsagesAsync(Document document, int at, bool declarations, SourceText? within, CancellationToken cancel)
    {
        JsonArray found = [];
        if (await SymbolAsync(document, at, cancel).ConfigureAwait(false) is not { } symbol)
        {
            return found;
        }
        HashSet<(string Path, TextSpan Span)> seen = [];
        void Add(Location location, bool write)
        {
            if (!location.IsInSource || location.SourceTree is not { FilePath.Length: > 0 } tree || !seen.Add((tree.FilePath, location.SourceSpan)))
            {
                return;
            }
            if (within is null)
            {
                found.Add(Places.Location(location));
            }
            else if (tree.FilePath == document.FilePath)
            {
                found.Add(new JsonObject { ["range"] = Places.Range(within, location.SourceSpan), ["kind"] = write ? 3 : 2 });
            }
        }
        foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(symbol, document.Project.Solution, cancel).ConfigureAwait(false))
        {
            if (declarations)
            {
                foreach (Location declared in referenced.Definition.Locations)
                {
                    Add(declared, write: true);
                }
            }
            foreach (ReferenceLocation usage in referenced.Locations)
            {
                Add(usage.Location, write: false);
            }
        }
        return found;
    }

    private static async Task<JsonNode?> RenamableAsync(Document document, SourceText text, int at, CancellationToken cancel)
    {
        if (await SymbolAsync(document, at, cancel).ConfigureAwait(false) is not { } symbol || !symbol.Locations.Any(l => l.IsInSource)
            || await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root)
        {
            return null;
        }
        SyntaxToken token = root.FindToken(at);
        if (!token.Span.IntersectsWith(at) && at > 0)
        {
            token = root.FindToken(at - 1);
        }
        return string.Equals(token.ValueText, symbol.Name, StringComparison.OrdinalIgnoreCase)
            ? new JsonObject { ["range"] = Places.Range(text, token.Span), ["placeholder"] = token.ValueText }
            : null;
    }

    private static async Task<JsonNode?> RenamedAsync(Document document, int at, string name, CancellationToken cancel)
    {
        if (name.Length == 0 || await SymbolAsync(document, at, cancel).ConfigureAwait(false) is not { } symbol)
        {
            return null;
        }
        Solution before = document.Project.Solution;
        Solution after = await Renamer.RenameSymbolAsync(before, symbol, new SymbolRenameOptions(), name, cancel).ConfigureAwait(false);
        JsonObject changes = [];
        foreach (ProjectChanges project in after.GetChanges(before).GetProjectChanges())
        {
            foreach (DocumentId id in project.GetChangedDocuments())
            {
                if (before.GetDocument(id) is not { FilePath: { } path } was || after.GetDocument(id) is not { } now || changes.ContainsKey(Places.UriOf(path)))
                {
                    continue;
                }
                SourceText text = await was.GetTextAsync(cancel).ConfigureAwait(false);
                changes[Places.UriOf(path)] = Edits(text, await now.GetTextChangesAsync(was, cancel).ConfigureAwait(false));
            }
        }
        return new JsonObject { ["changes"] = changes };
    }

    private static async Task<JsonNode?> FormattedAsync(Document document, SourceText text, TextSpan? span, JsonNode? asked, CancellationToken cancel)
    {
        OptionSet options = document.Project.Solution.Options;
        if ((int?)asked?["tabSize"] is { } size and > 0)
        {
            options = options.WithChangedOption(FormattingOptions.TabSize, LanguageNames.VisualBasic, size)
                .WithChangedOption(FormattingOptions.IndentationSize, LanguageNames.VisualBasic, size);
        }
        if ((bool?)asked?["insertSpaces"] is { } spaces)
        {
            options = options.WithChangedOption(FormattingOptions.UseTabs, LanguageNames.VisualBasic, !spaces);
        }
        Document formatted = span is { } part
            ? await Formatter.FormatAsync(document, part, options, cancel).ConfigureAwait(false)
            : await Formatter.FormatAsync(document, options, cancel).ConfigureAwait(false);
        return Edits(text, await formatted.GetTextChangesAsync(document, cancel).ConfigureAwait(false));
    }

    private static JsonArray Edits(SourceText text, IEnumerable<TextChange> changes)
    {
        JsonArray edits = [];
        foreach (TextChange change in changes)
        {
            edits.Add(new JsonObject { ["range"] = Places.Range(text, change.Span), ["newText"] = change.NewText ?? string.Empty });
        }
        return edits;
    }
}
