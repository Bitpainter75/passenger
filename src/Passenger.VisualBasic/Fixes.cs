using System.Collections.Immutable;
using System.Composition.Hosting;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace Passenger.VisualBasic;

/// <summary>The fixes and refactorings Roslyn has for a place in a Visual Basic file, as the protocol's code actions: a fix
/// for each diagnostic there from the providers that fix its id, then the refactorings its selection takes. An action is
/// listed by its title alone and changes nothing until it is resolved, when Roslyn computes its edit; one that would do
/// more than change the solution's texts (open a file, run a command) is resolved with the texts' part.</summary>
internal sealed class Fixes
{
    /// <summary>Roslyn's parts composed once: the workspace's services, and the providers found among them.</summary>
    private static readonly CompositionHost Container = new ContainerConfiguration().WithAssemblies(MefHostServices.DefaultAssemblies).CreateContainer();

    public static MefHostServices Host { get; } = MefHostServices.Create(Container);

    private static readonly Lazy<ImmutableArray<CodeFixProvider>> Fixers = new(() =>
        [.. Container.GetExports<CodeFixProvider>().Where(p => p.GetType().GetCustomAttribute<ExportCodeFixProviderAttribute>()?.Languages.Contains(LanguageNames.VisualBasic) == true)]);

    private static readonly Lazy<ImmutableArray<CodeRefactoringProvider>> Refactorings = new(() =>
        [.. Container.GetExports<CodeRefactoringProvider>()
            .Where(p => p.GetType().GetCustomAttribute<ExportCodeRefactoringProviderAttribute>()?.Languages.Contains(LanguageNames.VisualBasic) == true)]);

    /// <summary>The actions last listed and the solution they were listed for, which a resolve of one of them reads.</summary>
    private (Solution Solution, ImmutableArray<CodeAction> Actions)? listed;

    public async Task<JsonNode?> OfAsync(Document document, SourceText text, TextSpan span, CancellationToken cancel)
    {
        List<(CodeAction Action, string Kind)> found = [];
        TextSpan lines = TextSpan.FromBounds(text.Lines.GetLineFromPosition(span.Start).Start, text.Lines.GetLineFromPosition(span.End).End);
        foreach (Diagnostic diagnostic in (await Checks.OfAsync(document, cancel).ConfigureAwait(false)).Where(d => d.Location.SourceSpan.IntersectsWith(lines)))
        {
            foreach (CodeFixProvider fixer in Fixers.Value.Where(f => f.FixableDiagnosticIds.Contains(diagnostic.Id)))
            {
                CodeFixContext context = new(document, diagnostic, (action, _) => found.Add((action, "quickfix")), cancel);
                await Asked(() => fixer.RegisterCodeFixesAsync(context)).ConfigureAwait(false);
            }
        }
        foreach (CodeRefactoringProvider refactoring in Refactorings.Value)
        {
            CodeRefactoringContext context = new(document, span, action => found.Add((action, "refactor")), cancel);
            await Asked(() => refactoring.ComputeRefactoringsAsync(context)).ConfigureAwait(false);
        }
        List<(CodeAction Action, string Kind, string Title)> flat = [];
        foreach ((CodeAction action, string kind) in found)
        {
            // One that only holds others is listed as each of them, under its own words.
            if (action.NestedActions.IsEmpty)
            {
                flat.Add((action, kind, action.Title));
            }
            flat.AddRange(action.NestedActions.Select(nested => (nested, kind, $"{action.Title}: {nested.Title}")));
        }
        flat = [.. flat.DistinctBy(f => (f.Kind, f.Title))];
        listed = (document.Project.Solution, [.. flat.Select(f => f.Action)]);
        JsonArray actions = [];
        for (int i = 0; i < flat.Count; i++)
        {
            actions.Add(new JsonObject { ["title"] = flat[i].Title, ["kind"] = flat[i].Kind, ["data"] = i });
        }
        return actions;
    }

    public async Task<JsonNode?> ResolvedAsync(JsonNode? action, CancellationToken cancel)
    {
        if (action is not JsonObject asked || listed is not var (before, actions) || (int?)asked["data"] is not { } index || index < 0 || index >= actions.Length)
        {
            return action?.DeepClone();
        }
        JsonObject resolved = (JsonObject)asked.DeepClone();
        foreach (CodeActionOperation operation in await actions[index].GetOperationsAsync(cancel).ConfigureAwait(false))
        {
            if (operation is ApplyChangesOperation changes)
            {
                resolved["edit"] = new JsonObject { ["changes"] = await Answers.ChangesAsync(before, changes.ChangedSolution, cancel).ConfigureAwait(false) };
            }
        }
        return resolved;
    }

    /// <summary>A provider asked, whose failure costs its own actions alone: it is Roslyn's code or an analyzer's, run for
    /// any text a person has typed.</summary>
    private static async Task Asked(Func<Task> ask)
    {
        try
        {
            await ask().ConfigureAwait(false);
        }
        catch (Exception failed) when (failed is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync($"A code action's provider failed: {failed}").ConfigureAwait(false);
        }
    }
}
