using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Passenger.Core;

namespace Passenger.VisualBasic;

/// <summary>What is said of a file beyond the compiler's own errors: its project's analyzers (the SDK's, a package's, each
/// loaded once from the file its build names) and Roslyn's own for Visual Basic (unused imports, simplifications), each
/// held to the severity the project's .editorconfig gives. An analyzer that cannot load or fails says nothing.</summary>
internal static class Checks
{
    /// <summary>What an analyzer that threw is reported as, which no person is to read as their code's.</summary>
    private const string AnalyzerFailed = "AD0001";

    private static readonly Loader Loading = new();
    private static readonly Dictionary<string, AnalyzerReference> Loaded = new(PathNames.Comparer);

    /// <summary>Roslyn's own analyzers for Visual Basic, made once: every one of its features' assemblies that can be made.</summary>
    public static AnalyzerReference BuiltIn { get; } = new AnalyzerImageReference(
    [
        .. new[] { "Microsoft.CodeAnalysis.Features", "Microsoft.CodeAnalysis.VisualBasic.Features" }.SelectMany(name => Assembly.Load(name).GetTypes())
            .Where(type => !type.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(type)
                && type.GetCustomAttribute<DiagnosticAnalyzerAttribute>() is { } named && named.Languages.Contains(LanguageNames.VisualBasic))
            .Select(Made).OfType<DiagnosticAnalyzer>(),
    ]);

    /// <summary>The analyzers of a file a build names, the same reference for every project that names it.</summary>
    public static AnalyzerReference Of(string path)
    {
        lock (Loaded)
        {
            if (!Loaded.TryGetValue(path, out AnalyzerReference? reference))
            {
                Loaded[path] = reference = new AnalyzerFileReference(path, Loading);
            }
            return reference;
        }
    }

    /// <summary>A file's diagnostics, the compiler's and the analyzers', hidden ones too (a fix may be offered for one).</summary>
    public static async Task<ImmutableArray<Diagnostic>> OfAsync(Document document, CancellationToken cancel)
    {
        if (await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
        {
            return [];
        }
        ImmutableArray<Diagnostic> compiler = model.GetDiagnostics(cancellationToken: cancel);
        ImmutableArray<DiagnosticAnalyzer> analyzers = [.. document.Project.AnalyzerReferences.SelectMany(r => r.GetAnalyzers(LanguageNames.VisualBasic)).Distinct()];
        if (analyzers.IsEmpty)
        {
            return compiler;
        }
        CompilationWithAnalyzers analysed = model.Compilation.WithAnalyzers(analyzers,
            new CompilationWithAnalyzersOptions(document.Project.AnalyzerOptions, onAnalyzerException: null, concurrentAnalysis: true, logAnalyzerExecutionTime: false));
        ImmutableArray<Diagnostic> syntax = await analysed.GetAnalyzerSyntaxDiagnosticsAsync(model.SyntaxTree, cancel).ConfigureAwait(false);
        ImmutableArray<Diagnostic> semantic = await analysed.GetAnalyzerSemanticDiagnosticsAsync(model, filterSpan: null, cancel).ConfigureAwait(false);
        return [.. compiler, .. syntax.Concat(semantic).Where(d => d.Id != AnalyzerFailed && d.Location.SourceTree == model.SyntaxTree)];
    }

    private static object? Made(Type type)
    {
        try
        {
            return type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, []) is null ? null : Activator.CreateInstance(type, nonPublic: true);
        }
        catch (Exception failed) when (failed is TargetInvocationException or MemberAccessException or TypeLoadException)
        {
            return null;
        }
    }

    /// <summary>Loads an analyzer's assembly and those beside it that it names, apart from the server's own; Roslyn itself is
    /// the server's, whatever version the analyzer was compiled against.</summary>
    private sealed class Loader : AssemblyLoadContext, IAnalyzerAssemblyLoader
    {
        private readonly HashSet<string> folders = new(PathNames.Comparer);

        public Loader()
            : base("Analyzers")
        {
        }

        public void AddDependencyLocation(string fullPath)
        {
            lock (folders)
            {
                folders.Add(Path.GetDirectoryName(fullPath) ?? string.Empty);
            }
        }

        public Assembly LoadFromPath(string fullPath)
        {
            AddDependencyLocation(fullPath);
            return LoadFromAssemblyPath(fullPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is not { } name || name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) && Default.Assemblies.Any(a => a.GetName().Name == name))
            {
                return null;
            }
            string[] beside;
            lock (folders)
            {
                beside = [.. folders];
            }
            return beside.Select(folder => Path.Combine(folder, name + ".dll")).FirstOrDefault(File.Exists) is { } found ? LoadFromAssemblyPath(found) : null;
        }
    }
}
