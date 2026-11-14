using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Passenger.Core;

namespace Passenger.VisualBasic;

/// <summary>The Visual Basic projects the client gave and the files it has open, as one Roslyn solution. A project comes
/// as its design-time builds (the project host's JSON, one a target framework): its compiler's command line is parsed as
/// vbc parses it, so its options, imports, root namespace and references are the build's own. An open file's text is the
/// client's, every other file's the disk's; an open file of no project is one of a project of its own, with the
/// references of the runtime this server runs on, so it is still parsed and completed.</summary>
internal sealed class Projects
{
    private const string LooseName = "Miscellaneous Files";

    private readonly AdhocWorkspace workspace = new(MefHostServices.DefaultHost);
    private readonly Dictionary<string, List<Loaded>> loaded = new(PathNames.Comparer);
    private readonly Dictionary<string, SourceText> open = new(PathNames.Comparer);
    private readonly Dictionary<string, PortableExecutableReference> references = new(PathNames.Comparer);
    private readonly Dictionary<string, string?> given = new(PathNames.Comparer);
    private ProjectId? loose;

    /// <summary>One design-time build's project: what its command line referenced, which projects it names and what it writes.</summary>
    private sealed record Loaded(ProjectId Id, ImmutableArray<(string Path, MetadataReferenceProperties Properties)> References, string[] ProjectReferences,
        string?[] Outputs);

    public Solution Solution => workspace.CurrentSolution;

    public IReadOnlyCollection<string> Open => open.Keys;

    /// <summary>A file's documents, one for each project (a target framework's) that compiles it: the first is the one asked.</summary>
    public ImmutableArray<DocumentId> DocumentsOf(string path) => Solution.GetDocumentIdsWithFilePath(path);

    public Document? DocumentOf(string path) => DocumentsOf(path) is [DocumentId first, ..] ? Solution.GetDocument(first) : null;

    /// <summary>Takes a project's design-time builds in place of those it had; none, and the project is forgotten.</summary>
    public void Load(string path, string? infos)
    {
        // The builds it has already: its compilations stand, so a save that changed no project costs nothing here.
        if (given.TryGetValue(path, out string? held) && held == infos)
        {
            return;
        }
        given[path] = infos;
        Solution solution = Solution;
        if (loaded.Remove(path, out List<Loaded>? before))
        {
            foreach (Loaded project in before)
            {
                solution = solution.RemoveProject(project.Id);
            }
        }
        List<Loaded> now = [];
        if (infos is not null && JsonNode.Parse(infos) is JsonArray builds)
        {
            foreach (JsonNode? build in builds)
            {
                if (build is JsonObject info && (string?)info["language"] == "Visual Basic")
                {
                    solution = Add(solution, path, info, now);
                }
            }
        }
        if (now.Count > 0)
        {
            loaded[path] = now;
        }
        Apply(Loose(Linked(solution)));
    }

    public void OpenFile(string path, string text)
    {
        open[path] = SourceText.From(text);
        Apply(Loose(WithText(Solution, path)));
    }

    /// <summary>An open file's text changed by the client: whole, or in the ranges it names, in order.</summary>
    public void Change(string path, JsonArray changes)
    {
        if (!open.TryGetValue(path, out SourceText? text))
        {
            return;
        }
        foreach (JsonNode? change in changes)
        {
            string written = (string?)change?["text"] ?? string.Empty;
            text = change?["range"] is { } range ? text.Replace(Places.Span(text, range), written) : SourceText.From(written);
        }
        open[path] = text;
        Apply(WithText(Solution, path));
    }

    public void Close(string path)
    {
        if (open.Remove(path))
        {
            Apply(Loose(WithText(Solution, path)));
        }
    }

    /// <summary>Files the client heard change on disk: those not open are read again.</summary>
    public void Changed(IEnumerable<string> paths)
    {
        Solution solution = Solution;
        foreach (string path in paths.Where(p => !open.ContainsKey(p)))
        {
            solution = WithText(solution, path);
        }
        Apply(solution);
    }

    private void Apply(Solution solution)
    {
        if (solution != Solution && !workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("The workspace did not take a change of its own solution");
        }
    }

    private Solution Add(Solution solution, string path, JsonObject info, List<Loaded> now)
    {
        string folder = Path.GetDirectoryName(path) ?? ".";
        string[] arguments = Strings(info["commandLineArgs"]);
        VisualBasicCommandLineArguments parsed = VisualBasicCommandLineParser.Default.Parse(arguments, folder, sdkDirectory: null);
        string name = Path.GetFileNameWithoutExtension(path);
        string? framework = (string?)info["targetFramework"];
        ProjectId id = ProjectId.CreateNewId(path);
        ProjectInfo project = ProjectInfo.Create(id, VersionStamp.Create(), framework is null ? name : $"{name} ({framework})", parsed.CompilationName ?? name,
                LanguageNames.VisualBasic, filePath: path, outputFilePath: (string?)info["outputFilePath"],
                compilationOptions: parsed.CompilationOptions.WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default), parseOptions: parsed.ParseOptions.WithDocumentationMode(DocumentationMode.Parse))
            .WithDocuments(Documents(id, info["documents"]))
            .WithAdditionalDocuments(Documents(id, info["additionalDocuments"]))
            .WithAnalyzerConfigDocuments(Documents(id, info["analyzerConfigDocuments"]));
        now.Add(new Loaded(id, [.. parsed.MetadataReferences.Select(r => (Path.GetFullPath(r.Reference, folder), r.Properties))],
            [.. (info["projectReferences"] as JsonArray ?? []).Select(r => (string?)r?["path"]).OfType<string>().Select(r => Path.GetFullPath(r, folder))],
            [(string?)info["outputFilePath"], (string?)info["outputRefFilePath"], (string?)info["intermediateOutputFilePath"]]));
        return solution.AddProject(project);
    }

    private List<DocumentInfo> Documents(ProjectId project, JsonNode? listed)
    {
        List<DocumentInfo> documents = [];
        foreach (JsonNode? document in listed as JsonArray ?? [])
        {
            if ((string?)document?["filePath"] is { Length: > 0 } path)
            {
                documents.Add(DocumentInfo.Create(DocumentId.CreateNewId(project, path), Path.GetFileName(path), Strings(document["folders"]), SourceCodeKind.Regular,
                    Loader(path), path, (bool?)document["isGenerated"] ?? false));
            }
        }
        return documents;
    }

    private TextLoader Loader(string path) =>
        open.TryGetValue(path, out SourceText? text) ? TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), path)) : new FileTextLoader(path, defaultEncoding: null);

    /// <summary>Each project's references as its builds name them: a project loaded here by itself, never by the assembly
    /// it writes too, which would be the same types twice; a file by one reader, kept while the file stands.</summary>
    private Solution Linked(Solution solution)
    {
        foreach (Loaded project in loaded.Values.SelectMany(p => p))
        {
            List<Loaded> named = [.. project.ProjectReferences.Where(loaded.ContainsKey).Select(r => loaded[r][0])];
            HashSet<string> written = new(named.SelectMany(n => n.Outputs).OfType<string>(), PathNames.Comparer);
            solution = solution
                .WithProjectReferences(project.Id, named.Select(n => new ProjectReference(n.Id)))
                .WithProjectMetadataReferences(project.Id, project.References.Where(r => !written.Contains(r.Path) && File.Exists(r.Path))
                    .DistinctBy(r => r.Path, PathNames.Comparer).Select(r => Reference(r.Path, r.Properties)));
        }
        return solution;
    }

    private MetadataReference Reference(string path, MetadataReferenceProperties properties)
    {
        if (!references.TryGetValue(path, out PortableExecutableReference? reference))
        {
            string documentation = Path.ChangeExtension(path, ".xml");
            reference = MetadataReference.CreateFromFile(path, documentation: File.Exists(documentation) ? XmlDocumentationProvider.CreateFromFile(documentation) : null);
            references[path] = reference;
        }
        return reference.WithProperties(properties);
    }

    /// <summary>The solution with the documents of a path holding what the client has of it, else what the disk has.</summary>
    private Solution WithText(Solution solution, string path)
    {
        foreach (DocumentId id in solution.GetDocumentIdsWithFilePath(path))
        {
            solution = solution.WithDocumentTextLoader(id, Loader(path), PreservationMode.PreserveValue);
        }
        return solution;
    }

    /// <summary>The solution with every open file of no loaded project in the project of loose files, and no other there.</summary>
    private Solution Loose(Solution solution)
    {
        if (loose is { } id && solution.GetProject(id) is { } before)
        {
            foreach (Document document in before.Documents)
            {
                string? path = document.FilePath;
                if (path is null || !open.ContainsKey(path) || solution.GetDocumentIdsWithFilePath(path).Length > 1)
                {
                    solution = solution.RemoveDocument(document.Id);
                }
            }
        }
        foreach (string path in open.Keys)
        {
            if (!solution.GetDocumentIdsWithFilePath(path).IsEmpty)
            {
                continue;
            }
            if (loose is null || solution.GetProject(loose) is null)
            {
                loose = ProjectId.CreateNewId(LooseName);
                solution = solution.AddProject(ProjectInfo.Create(loose, VersionStamp.Create(), LooseName, LooseName, LanguageNames.VisualBasic,
                    parseOptions: VisualBasicParseOptions.Default.WithDocumentationMode(DocumentationMode.Parse),
                    compilationOptions: new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optionInfer: true,
                        globalImports: GlobalImport.Parse("Microsoft.VisualBasic", "System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks")),
                    metadataReferences: Runtime()));
            }
            solution = solution.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(loose, path), Path.GetFileName(path), loader: Loader(path), filePath: path));
        }
        return solution;
    }

    /// <summary>The assemblies of the runtime this server runs on, which a file of no project is read against.</summary>
    private IEnumerable<MetadataReference> Runtime()
    {
        string folder = Path.GetDirectoryName(typeof(object).Assembly.Location) ?? string.Empty;
        string trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        return trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => PathNames.Same(Path.GetDirectoryName(path) ?? string.Empty, folder) && File.Exists(path))
            .Select(path => Reference(path, MetadataReferenceProperties.Assembly));
    }

    private static string[] Strings(JsonNode? listed) => [.. (listed as JsonArray ?? []).Select(s => (string?)s).OfType<string>()];
}
