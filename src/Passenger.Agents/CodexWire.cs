using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.Core;

namespace Passenger.Agents;

/// <summary>Codex's protocol, its app server's (<c>codex app-server</c>): JSON-RPC a line each with no "jsonrpc"
/// member, as its own apps speak it. After <c>initialize</c> a thread is started in the folder, each prompt a turn
/// (<c>turn/start</c>) whose items come as notifications: a message's deltas, a command, a patch, an MCP tool, each
/// started and completed. It asks leave by requests of its own (<c>item/commandExecution/requestApproval</c>,
/// <c>item/fileChange/requestApproval</c>), answered by a decision; a request this client has no answer to is refused,
/// so no turn waits on it. The thread takes the person's own configuration: its model, sandbox and when it asks.</summary>
public sealed class CodexWire : AgentWire
{
    private const string Initialize = "initialize";
    private const string ThreadStart = "thread/start";
    private const string TurnStart = "turn/start";
    private const string TurnInterrupt = "turn/interrupt";

    /// <summary>The requests sent and not answered, by id, each by its method.</summary>
    private readonly Dictionary<int, string> sent = [];

    /// <summary>The prompts written before the thread was there to take them.</summary>
    private readonly List<string> waiting = [];

    /// <summary>The questions asked, by the id shown, each with the request's id as the program wrote it.</summary>
    private readonly Dictionary<string, string> asked = new(StringComparer.Ordinal);

    /// <summary>The files each patch begun changes, by item, for the question that asks leave for it.</summary>
    private readonly Dictionary<string, IReadOnlyList<AgentFileEdit>> patches = new(StringComparer.Ordinal);

    private string folder = string.Empty;
    private string? thread;
    private string? turn;
    private int requests;

    /// <summary>Whether a stop was asked before the turn it stops had told its id, to be sent once it has.</summary>
    private bool stopping;

    public override IReadOnlyList<string> Arguments(AgentStart start)
    {
        List<string> arguments = ["app-server"];
        if (start.Passenger is { } server)
        {
            // Each value is TOML, which reads a JSON string and a JSON list of strings as its own.
            arguments.AddRange(["-c", $"mcp_servers.{AgentStart.ServerName}.command={JsonSerializer.Serialize(server.Command)}",
                "-c", $"mcp_servers.{AgentStart.ServerName}.args={JsonSerializer.Serialize(server.Arguments)}"]);
        }
        return arguments;
    }

    public override IReadOnlyList<string> Begin(AgentStart start)
    {
        folder = start.Folder;
        return
        [
            Request(Initialize, new JsonObject
            {
                ["clientInfo"] = new JsonObject { ["name"] = "passenger", ["title"] = "Passenger", ["version"] = Product.Version },
            }),
        ];
    }

    private string Request(string method, JsonObject parameters)
    {
        sent[++requests] = method;
        return Line(new JsonObject { ["id"] = requests, ["method"] = method, ["params"] = parameters });
    }

    public override IReadOnlyList<string> Prompt(string text)
    {
        if (thread is null)
        {
            waiting.Add(text);
            return [];
        }
        return [Turn(text)];
    }

    private string Turn(string text) => Request(TurnStart, new JsonObject
    {
        ["threadId"] = thread,
        ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text, ["text_elements"] = new JsonArray() }),
    });

    public override IReadOnlyList<string> Answer(string id, AgentAnswer answer) => !asked.Remove(id, out string? request) ? [] :
    [
        Line(new JsonObject
        {
            ["id"] = JsonNode.Parse(request),
            ["result"] = new JsonObject
            {
                ["decision"] = answer switch { AgentAnswer.Allow => "accept", AgentAnswer.AllowAlways => "acceptForSession", _ => "decline" },
            },
        }),
    ];

    public override IReadOnlyList<string> Interrupt()
    {
        if (turn is null)
        {
            // Prompts not yet sent are taken back; one sent is stopped as its turn tells its id.
            stopping = waiting.Count == 0 && sent.ContainsValue(TurnStart);
            waiting.Clear();
            return [];
        }
        return [Request(TurnInterrupt, new JsonObject { ["threadId"] = thread, ["turnId"] = turn })];
    }

    /// <summary>A turn told its id: a stop asked before goes to it now.</summary>
    private void Began(string? id, List<string> replies)
    {
        turn = id;
        if (stopping && id is not null)
        {
            stopping = false;
            replies.AddRange(Interrupt());
        }
    }

    protected override void Read(JsonElement message, List<AgentEvent> events, List<string> replies)
    {
        JsonElement? parameters = JsonMembers.Object(message, "params");
        if (JsonMembers.Text(message, "method") is { } method)
        {
            // Another thread's word (a subagent's) is no part of this conversation.
            bool own = JsonMembers.Text(parameters, "threadId") is not { } named || named == thread;
            if (JsonMembers.Value(message, "id") is { } id)
            {
                Asked(method, id.GetRawText(), parameters, own, events, replies);
            }
            else if (own)
            {
                Told(method, parameters, events, replies);
            }
        }
        else if (JsonMembers.Number(message, "id") is { } answered && sent.Remove(answered, out string? asking))
        {
            Answered(asking, message, events, replies);
        }
    }

    /// <summary>The answer to a request of this client's: the next step of the start, a turn's id, or why it failed.</summary>
    private void Answered(string method, JsonElement message, List<AgentEvent> events, List<string> replies)
    {
        JsonElement? result = JsonMembers.Object(message, "result");
        if (JsonMembers.Text(JsonMembers.Object(message, "error"), "message") is { } failed)
        {
            // A turn already over cannot be interrupted, which is no failure to tell.
            if (method != TurnInterrupt)
            {
                events.Add(method == TurnStart ? new AgentTurnEnded(AgentTurnEnd.Failed, failed) : new AgentNotice(failed, IsError: true));
            }
            return;
        }
        switch (method)
        {
            case Initialize:
                replies.Add(Line(new JsonObject { ["method"] = "initialized" }));
                replies.Add(Request(ThreadStart, new JsonObject { ["cwd"] = folder }));
                break;
            case ThreadStart:
                thread = JsonMembers.Text(JsonMembers.Object(result, "thread"), "id");
                events.Add(new AgentStarted(JsonMembers.Text(result, "model")));
                replies.AddRange(waiting.Select(Turn));
                waiting.Clear();
                break;
            case TurnStart when turn is null:
                Began(JsonMembers.Text(JsonMembers.Object(result, "turn"), "id"), replies);
                break;
        }
    }

    private void Told(string method, JsonElement? parameters, List<AgentEvent> events, List<string> replies)
    {
        JsonElement? item = JsonMembers.Object(parameters, "item");
        switch (method)
        {
            case "turn/started" when turn is null:
                Began(JsonMembers.Text(JsonMembers.Object(parameters, "turn"), "id"), replies);
                break;
            case "item/agentMessage/delta" when JsonMembers.Text(parameters, "itemId") is { } message && JsonMembers.Text(parameters, "delta") is { Length: > 0 } more:
                events.Add(new AgentWords(message, more));
                break;
            case "item/started" when JsonMembers.Text(item, "id") is { } id:
                Started(id, item, events);
                break;
            case "item/completed" when JsonMembers.Text(item, "id") is { } id:
                Completed(id, item, events);
                break;
            case "serverRequest/resolved" when JsonMembers.Value(parameters, "requestId") is { } request:
                foreach (string id in asked.Where(a => a.Value == request.GetRawText()).Select(a => a.Key).ToList())
                {
                    asked.Remove(id);
                    events.Add(new AgentAskEnded(id));
                }
                break;
            case "turn/completed":
                Ended(JsonMembers.Object(parameters, "turn"), events);
                break;
            case "error" when JsonMembers.Bool(parameters, "willRetry") == true && JsonMembers.Text(JsonMembers.Object(parameters, "error"), "message") is { } retried:
                events.Add(new AgentNotice(retried, IsError: false));
                break;
            case "warning" or "guardianWarning" when JsonMembers.Text(parameters, "message") is { } warned:
                events.Add(new AgentNotice(warned, IsError: false));
                break;
            case "configWarning" when JsonMembers.Text(parameters, "summary") is { } summary:
                events.Add(new AgentNotice(JsonMembers.Text(parameters, "details") is { Length: > 0 } details ? $"{summary}: {details}" : summary, IsError: false));
                break;
        }
    }

    private void Started(string id, JsonElement? item, List<AgentEvent> events)
    {
        switch (JsonMembers.Text(item, "type"))
        {
            case "commandExecution":
                events.Add(new AgentToolStarted(id, "Run", Cut(JsonMembers.Text(item, "command") ?? string.Empty), []));
                break;
            case "fileChange":
                IReadOnlyList<AgentFileEdit> edits = patches[id] = Edits(item);
                events.Add(new AgentToolStarted(id, "Edit", string.Join('\n', edits.Select(e => e.Path)), edits));
                break;
            case "mcpToolCall":
                events.Add(new AgentToolStarted(id, $"{JsonMembers.Text(item, "server")}: {JsonMembers.Text(item, "tool")}", Cut(Shown(JsonMembers.Value(item, "arguments"))), []));
                break;
            case "webSearch":
                events.Add(new AgentToolStarted(id, "Search the Web", JsonMembers.Text(item, "query") ?? string.Empty, []));
                break;
        }
    }

    private void Completed(string id, JsonElement? item, List<AgentEvent> events)
    {
        bool failed = JsonMembers.Text(item, "status") is "failed" or "declined";
        switch (JsonMembers.Text(item, "type"))
        {
            case "agentMessage" when JsonMembers.Text(item, "text") is { } text:
                events.Add(new AgentMessage(id, text));
                break;
            case "commandExecution":
                events.Add(new AgentToolEnded(id, failed, Cut(JsonMembers.Text(item, "aggregatedOutput")?.TrimEnd() ?? string.Empty), []));
                break;
            case "fileChange":
                patches.Remove(id);
                events.Add(new AgentToolEnded(id, failed, null, Edits(item)));
                break;
            case "mcpToolCall":
                string said = JsonMembers.Text(JsonMembers.Object(item, "error"), "message")
                    ?? string.Join('\n', JsonMembers.Items(JsonMembers.Object(item, "result"), "content").Select(c => JsonMembers.Text(c, "text")).Where(t => t is not null));
                events.Add(new AgentToolEnded(id, failed, Cut(said), []));
                break;
            case "webSearch":
                events.Add(new AgentToolEnded(id, false, null, []));
                break;
        }
    }

    /// <summary>A patch's files: an added or deleted file's text as added or deleted lines, a changed one's diff from its
    /// first hunk on.</summary>
    private static IReadOnlyList<AgentFileEdit> Edits(JsonElement? item)
    {
        List<AgentFileEdit> edits = [];
        foreach (JsonElement change in JsonMembers.Items(item, "changes"))
        {
            if (JsonMembers.Text(change, "path") is not { } path)
            {
                continue;
            }
            string diff = JsonMembers.Text(change, "diff") ?? string.Empty;
            AgentFileChange kind = JsonMembers.Text(JsonMembers.Object(change, "kind"), "type") switch
            {
                "add" => AgentFileChange.Added,
                "delete" => AgentFileChange.Deleted,
                _ => AgentFileChange.Changed,
            };
            edits.Add(new AgentFileEdit(path, kind, Cut(kind == AgentFileChange.Changed ? Hunks(diff) : Marked(diff, kind == AgentFileChange.Added ? '+' : '-'))));
        }
        return edits;
    }

    private static string Marked(string text, char mark)
    {
        StringBuilder marked = new();
        foreach (string line in text.TrimEnd('\n').Split('\n'))
        {
            marked.Append(mark).Append(line.TrimEnd('\r')).Append('\n');
        }
        return marked.ToString().TrimEnd('\n');
    }

    /// <summary>A unified diff without the file names over its first hunk.</summary>
    private static string Hunks(string diff)
    {
        int first = diff.StartsWith("@@", StringComparison.Ordinal) ? 0 : diff.IndexOf("\n@@", StringComparison.Ordinal) + 1;
        return diff[first..].TrimEnd('\n');
    }

    /// <summary>A request of the program's: leave for a command or a patch is the person's to give; any other is refused.</summary>
    private void Asked(string method, string request, JsonElement? parameters, bool own, List<AgentEvent> events, List<string> replies)
    {
        string id = $"ask-{request}";
        string? reason = JsonMembers.Text(parameters, "reason");
        switch (method)
        {
            case "item/commandExecution/requestApproval" when own:
                asked[id] = request;
                string command = JsonMembers.Text(parameters, "command") ?? string.Empty;
                events.Add(new AgentAsked(id, "Run", Cut(reason is { Length: > 0 } ? $"{command}\n{reason}" : command), OffersAlways: true));
                break;
            case "item/fileChange/requestApproval" when own:
                asked[id] = request;
                string files = JsonMembers.Text(parameters, "itemId") is { } item && patches.TryGetValue(item, out IReadOnlyList<AgentFileEdit>? edits)
                    ? string.Join('\n', edits.Select(e => e.Path)) : string.Empty;
                events.Add(new AgentAsked(id, "Edit", reason is { Length: > 0 } ? $"{files}\n{reason}".Trim() : files, OffersAlways: true));
                break;
            default:
                replies.Add(Line(new JsonObject
                {
                    ["id"] = JsonNode.Parse(request),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Passenger's Agent window has no answer to {method}" },
                }));
                break;
        }
    }

    private void Ended(JsonElement? ended, List<AgentEvent> events)
    {
        turn = null;
        stopping = false;
        foreach (string id in asked.Keys.ToList())
        {
            asked.Remove(id);
            events.Add(new AgentAskEnded(id));
        }
        patches.Clear();
        events.Add(JsonMembers.Text(ended, "status") switch
        {
            "interrupted" => new AgentTurnEnded(AgentTurnEnd.Stopped),
            "failed" => new AgentTurnEnded(AgentTurnEnd.Failed, JsonMembers.Text(JsonMembers.Object(ended, "error"), "message")),
            _ => new AgentTurnEnded(AgentTurnEnd.Completed),
        });
    }
}
