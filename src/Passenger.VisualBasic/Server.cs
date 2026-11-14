using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;

namespace Passenger.VisualBasic;

/// <summary>The server's loop: each message read in turn, a notification acted on at once (an open file's text, a
/// project's builds), a request answered off the loop from the solution as it stood when the request came, so an answer
/// never reads text typed after it was asked for. A change is followed, once typing rests, by the open files'
/// diagnostics.</summary>
internal sealed class Server(Wire wire)
{
    /// <summary>The notification that gives or takes back projects: <c>{ projects: [{ path, infos }] }</c>, a project's
    /// infos the project host's design-time builds as JSON text, or null for a project to forget.</summary>
    public const string ProjectsMethod = "passenger/projects";

    /// <summary>How long typing rests before the open files are checked again.</summary>
    private static readonly TimeSpan Rest = TimeSpan.FromMilliseconds(300);

    private readonly Projects projects = new();
    private readonly Answers answers = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> asked = new(StringComparer.Ordinal);

    /// <summary>The version the client gave each open file's text, which a push of its diagnostics names.</summary>
    private readonly Dictionary<string, int> versions = new(PathNames.Comparer);
    private readonly CancellationTokenSource ending = new();
    private int changes;
    private bool shutdown;

    public async Task<int> RunAsync()
    {
        while (await wire.ReadAsync(ending.Token).ConfigureAwait(false) is { } message)
        {
            string? method = (string?)message["method"];
            JsonNode? id = message["id"];
            if (method == "exit")
            {
                break;
            }
            if (method is null)
            {
                continue;
            }
            if (id is null)
            {
                try
                {
                    Notified(method, message["params"]);
                }
                catch (Exception failed)
                {
                    // The server outlives one notification's failure, said where the client keeps a server's words.
                    await Console.Error.WriteLineAsync($"{method} failed: {failed}").ConfigureAwait(false);
                }
                continue;
            }
            CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(ending.Token);
            string key = id.ToJsonString();
            asked[key] = cancel;
            Solution solution = projects.Solution;
            // Off the loop, so a long answer never holds the next message, the word that cancels it among them.
            UnawaitedWork.Observe(BackgroundWork.Unheld(() => AnswerAsync(key, id.DeepClone(), method, message["params"], solution, cancel)));
        }
        await ending.CancelAsync().ConfigureAwait(false);
        return shutdown ? 0 : 1;
    }

    private void Notified(string method, JsonNode? parameters)
    {
        switch (method)
        {
            case "textDocument/didOpen":
                if (Places.DocumentOf(parameters) is { } opened)
                {
                    projects.OpenFile(opened, (string?)parameters?["textDocument"]?["text"] ?? string.Empty);
                    versions[opened] = (int?)parameters?["textDocument"]?["version"] ?? 0;
                }
                break;
            case "textDocument/didChange":
                if (Places.DocumentOf(parameters) is { } changed && parameters?["contentChanges"] is JsonArray edits)
                {
                    projects.Change(changed, edits);
                    versions[changed] = (int?)parameters?["textDocument"]?["version"] ?? 0;
                }
                break;
            case "textDocument/didClose":
                if (Places.DocumentOf(parameters) is { } closed)
                {
                    projects.Close(closed);
                    versions.Remove(closed);
                    UnawaitedWork.Observe(wire.WriteAsync(Notification("textDocument/publishDiagnostics",
                        new JsonObject { ["uri"] = Places.UriOf(closed), ["diagnostics"] = new JsonArray() })));
                }
                break;
            case "workspace/didChangeWatchedFiles":
                projects.Changed((parameters?["changes"] as JsonArray ?? []).Select(c => (string?)c?["uri"]).OfType<string>().Select(Places.PathOf));
                break;
            case ProjectsMethod:
                foreach (JsonNode? project in parameters?["projects"] as JsonArray ?? [])
                {
                    if ((string?)project?["path"] is { Length: > 0 } path)
                    {
                        projects.Load(path, (string?)project["infos"]);
                    }
                }
                break;
            case "$/cancelRequest":
                if (parameters?["id"] is { } id && asked.TryGetValue(id.ToJsonString(), out CancellationTokenSource? cancel))
                {
                    cancel.Cancel();
                }
                return;
            default:
                return;
        }
        int now = Interlocked.Increment(ref changes);
        Solution solution = projects.Solution;
        (string Path, int Version)[] open = [.. projects.Open.Select(path => (path, versions.GetValueOrDefault(path)))];
        // Off the loop, as a request's answer is: the check waits for typing to rest.
        UnawaitedWork.Observe(BackgroundWork.Unheld(() => CheckAsync(now, solution, open)));
    }

    private async Task AnswerAsync(string key, JsonNode id, string method, JsonNode? parameters, Solution solution, CancellationTokenSource cancel)
    {
        JsonObject answer = new() { ["jsonrpc"] = "2.0", ["id"] = id };
        try
        {
            if (method == "shutdown")
            {
                shutdown = true;
                answer["result"] = null;
            }
            else if (method == "initialize")
            {
                answer["result"] = Answers.Initialized();
            }
            else
            {
                answer["result"] = await answers.AnswerAsync(method, parameters, solution, cancel.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            answer["error"] = new JsonObject { ["code"] = -32800, ["message"] = "Cancelled" };
        }
        catch (Exception failed)
        {
            // The server outlives one request's failure, which the client is told whole.
            answer["error"] = Failure(failed);
        }
        finally
        {
            asked.TryRemove(key, out _);
            cancel.Dispose();
        }
        await wire.WriteAsync(answer).ConfigureAwait(false);
    }

    /// <summary>The open files' diagnostics published once typing rested, unless more was typed since.</summary>
    private async Task CheckAsync(int change, Solution solution, (string Path, int Version)[] open)
    {
        try
        {
            await Task.Delay(Rest, ending.Token).ConfigureAwait(false);
            foreach ((string path, int version) in open)
            {
                if (Volatile.Read(ref changes) != change)
                {
                    return;
                }
                if (solution.GetDocumentIdsWithFilePath(path) is [DocumentId id, ..] && solution.GetDocument(id) is { } document)
                {
                    JsonArray found = await Answers.DiagnosticsAsync(document, ending.Token).ConfigureAwait(false);
                    await wire.WriteAsync(Notification("textDocument/publishDiagnostics", new JsonObject { ["uri"] = Places.UriOf(path), ["version"] = version, ["diagnostics"] = found }))
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static JsonObject Failure(Exception failed) => new() { ["code"] = -32603, ["message"] = failed.ToString() };

    private static JsonObject Notification(string method, JsonObject parameters) => new() { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters };
}
