using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.Core;

namespace Passenger.Agents;

/// <summary>Claude Code's protocol, its print mode's stream of JSON both ways (<c>claude -p --input-format stream-json
/// --output-format stream-json</c>), as its own SDK speaks it: the person's messages go in as <c>user</c> lines, its
/// model's words come as <c>stream_event</c> deltas and whole <c>assistant</c> messages (one a content block), a tool's
/// result as a <c>user</c> line, a turn's end as <c>result</c>. With <c>--permission-prompt-tool stdio</c> it asks
/// leave for a tool by a <c>control_request</c> (<c>can_use_tool</c>), answered by a <c>control_response</c> giving the
/// tool's input back; an interrupt is a control request of the client's. The program stays for the whole conversation.</summary>
public sealed class ClaudeCodeWire : AgentWire
{
    /// <summary>The tool Claude Code asks the person a question with, whose form this client does not draw.</summary>
    private const string QuestionTool = "AskUserQuestion";

    /// <summary>What each question waits with: the tool's input, given back as it was to allow it, and the rules the
    /// program offers to keep so it need not ask again.</summary>
    private readonly Dictionary<string, (string Input, string? Always)> asked = new(StringComparer.Ordinal);

    /// <summary>The tools begun and not ended, by id, each with the file it writes where it writes one.</summary>
    private readonly Dictionary<string, string?> tools = new(StringComparer.Ordinal);

    /// <summary>The streamed texts not yet come whole, oldest first.</summary>
    private readonly Queue<string> streamed = new();

    /// <summary>The text block being streamed, by its index in its message.</summary>
    private readonly Dictionary<int, string> blocks = [];

    private int texts;
    private int requests;
    private bool working;

    /// <summary>Whether the turn under way was asked to stop: the program ends it as a failed one, naming no interrupt.</summary>
    private bool stopping;

    public override IReadOnlyList<string> Arguments(AgentStart start)
    {
        List<string> arguments = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--permission-prompt-tool", "stdio"];
        if (start.Passenger is { } server)
        {
            JsonArray given = [];
            foreach (string argument in server.Arguments)
            {
                given.Add(argument);
            }
            arguments.Add("--mcp-config");
            arguments.Add(Line(new JsonObject
            {
                ["mcpServers"] = new JsonObject { [AgentStart.ServerName] = new JsonObject { ["command"] = server.Command, ["args"] = given } },
            }));
        }
        return arguments;
    }

    public override IReadOnlyList<string> Begin(AgentStart start) => [];

    public override IReadOnlyList<string> Prompt(string text)
    {
        working = true;
        stopping = false;
        return [Line(new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = text } })];
    }

    public override IReadOnlyList<string> Answer(string id, AgentAnswer answer)
    {
        if (!asked.Remove(id, out (string Input, string? Always) waiting))
        {
            return [];
        }
        JsonObject said = answer == AgentAnswer.Deny
            ? new JsonObject { ["behavior"] = "deny", ["message"] = "The person at Passenger did not allow this." }
            : new JsonObject { ["behavior"] = "allow", ["updatedInput"] = JsonNode.Parse(waiting.Input) };
        if (answer == AgentAnswer.AllowAlways && waiting.Always is { } rules)
        {
            said["updatedPermissions"] = JsonNode.Parse(rules);
        }
        return [Answered(id, said)];
    }

    private static string Answered(string id, JsonObject said) => Line(new JsonObject
    {
        ["type"] = "control_response",
        ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = id, ["response"] = said },
    });

    public override IReadOnlyList<string> Interrupt()
    {
        if (!working)
        {
            return [];
        }
        stopping = true;
        return
        [
            Line(new JsonObject
            {
                ["type"] = "control_request",
                ["request_id"] = $"passenger-{++requests}",
                ["request"] = new JsonObject { ["subtype"] = "interrupt" },
            }),
        ];
    }

    protected override void Read(JsonElement message, List<AgentEvent> events, List<string> replies)
    {
        // A subagent's own messages are its tool's business: the conversation shows the tool.
        bool own = JsonMembers.Value(message, "parent_tool_use_id") is null or { ValueKind: JsonValueKind.Null };
        switch (JsonMembers.Text(message, "type"))
        {
            case "system" when JsonMembers.Text(message, "subtype") == "init":
                events.Add(new AgentStarted(JsonMembers.Text(message, "model")));
                break;
            case "stream_event" when own:
                Streamed(JsonMembers.Object(message, "event"), events);
                break;
            case "assistant" when own:
                foreach (JsonElement block in JsonMembers.Items(JsonMembers.Object(message, "message"), "content"))
                {
                    Said(block, events);
                }
                break;
            case "user" when own:
                foreach (JsonElement block in JsonMembers.Items(JsonMembers.Object(message, "message"), "content"))
                {
                    if (JsonMembers.Text(block, "type") == "tool_result" && JsonMembers.Text(block, "tool_use_id") is { } tool && tools.Remove(tool, out string? file))
                    {
                        events.Add(new AgentToolEnded(tool, JsonMembers.Bool(block, "is_error") == true, Cut(ResultText(block)),
                            Edits(JsonMembers.Value(message, "tool_use_result"), file)));
                    }
                }
                break;
            case "control_request":
                Asked(message, events, replies);
                break;
            case "result":
                Ended(message, events);
                break;
        }
    }

    private void Streamed(JsonElement? streamed, List<AgentEvent> events)
    {
        int index = JsonMembers.Number(streamed, "index") ?? 0;
        switch (JsonMembers.Text(streamed, "type"))
        {
            case "message_start":
                blocks.Clear();
                break;
            case "content_block_start" when JsonMembers.Text(JsonMembers.Object(streamed, "content_block"), "type") == "text":
                string id = $"text-{++texts}";
                blocks[index] = id;
                this.streamed.Enqueue(id);
                break;
            case "content_block_delta" when blocks.TryGetValue(index, out string? block)
                && JsonMembers.Text(JsonMembers.Object(streamed, "delta"), "text") is { Length: > 0 } more:
                events.Add(new AgentWords(block, more));
                break;
        }
    }

    /// <summary>A content block of the model's, whole: its text, or a tool it calls.</summary>
    private void Said(JsonElement block, List<AgentEvent> events)
    {
        switch (JsonMembers.Text(block, "type"))
        {
            case "text" when JsonMembers.Text(block, "text") is { } text:
                events.Add(new AgentMessage(streamed.TryDequeue(out string? begun) ? begun : $"text-{++texts}", text));
                break;
            case "tool_use" when JsonMembers.Text(block, "id") is { } id && JsonMembers.Text(block, "name") is { } name:
                JsonElement? input = JsonMembers.Object(block, "input");
                tools[id] = JsonMembers.Text(input, "file_path");
                events.Add(new AgentToolStarted(id, ToolTitle(name), Cut(Detail(name, input)), []));
                break;
        }
    }

    /// <summary>A tool's name as a person reads it: an MCP server's tool as "server: tool", where the program writes
    /// <c>mcp__server__tool</c>.</summary>
    private static string ToolTitle(string name) =>
        name.Split("__") is ["mcp", string server, .. string[] tool] && tool.Length > 0 ? $"{server}: {string.Join("__", tool)}" : name;

    /// <summary>What a tool works on, from its input: a command, a file, a pattern, a prompt's description, else its input
    /// as JSON.</summary>
    private static string Detail(string name, JsonElement? input) =>
        JsonMembers.Text(input, "command") ?? JsonMembers.Text(input, "file_path") ?? JsonMembers.Text(input, "pattern")
        ?? JsonMembers.Text(input, "description") ?? JsonMembers.Text(input, "url") ?? JsonMembers.Text(input, "query")
        ?? (input is { } given && given.EnumerateObject().Any() ? given.GetRawText() : string.Empty);

    /// <summary>A tool result's words: its text, or each of its text parts.</summary>
    private static string ResultText(JsonElement block) => JsonMembers.Value(block, "content") switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString()!,
        { ValueKind: JsonValueKind.Array } parts => string.Join('\n', parts.EnumerateArray().Select(p => JsonMembers.Text(p, "text")).Where(t => t is not null)),
        _ => string.Empty,
    };

    /// <summary>The file a writing tool changed, from the result the program keeps beside the model's: its patch's hunks,
    /// or a new file's content as added lines.</summary>
    private static IReadOnlyList<AgentFileEdit> Edits(JsonElement? result, string? file)
    {
        if ((JsonMembers.Text(result, "filePath") ?? file) is not { } path || JsonMembers.Value(result, "structuredPatch") is null)
        {
            return [];
        }
        StringBuilder diff = new();
        foreach (JsonElement hunk in JsonMembers.Items(result, "structuredPatch"))
        {
            diff.Append("@@ -").Append(JsonMembers.Number(hunk, "oldStart") ?? 0).Append(',').Append(JsonMembers.Number(hunk, "oldLines") ?? 0)
                .Append(" +").Append(JsonMembers.Number(hunk, "newStart") ?? 0).Append(',').Append(JsonMembers.Number(hunk, "newLines") ?? 0).Append(" @@\n");
            foreach (string line in JsonMembers.Texts(hunk, "lines"))
            {
                diff.Append(line).Append('\n');
            }
        }
        bool added = JsonMembers.Text(result, "type") == "create";
        if (added && JsonMembers.Text(result, "content") is { } content)
        {
            foreach (string line in content.TrimEnd('\n').Split('\n'))
            {
                diff.Append('+').Append(line.TrimEnd('\r')).Append('\n');
            }
        }
        return [new AgentFileEdit(path, added ? AgentFileChange.Added : AgentFileChange.Changed, Cut(diff.ToString().TrimEnd('\n')))];
    }

    private void Asked(JsonElement message, List<AgentEvent> events, List<string> replies)
    {
        JsonElement? request = JsonMembers.Object(message, "request");
        if (JsonMembers.Text(message, "request_id") is not { } id)
        {
            return;
        }
        if (JsonMembers.Text(request, "subtype") != "can_use_tool" || JsonMembers.Text(request, "tool_name") is not { } tool)
        {
            // A request this client has no answer to is refused, so the program never waits on it.
            replies.Add(Line(new JsonObject
            {
                ["type"] = "control_response",
                ["response"] = new JsonObject { ["subtype"] = "error", ["request_id"] = id, ["error"] = "Passenger has no answer to this request" },
            }));
            return;
        }
        JsonElement? input = JsonMembers.Object(request, "input");
        if (tool == QuestionTool)
        {
            replies.Add(Answered(id, new JsonObject
            {
                ["behavior"] = "deny",
                ["message"] = "Passenger's Agent window shows no question form: ask the person in plain words and end your turn.",
            }));
            return;
        }
        string? always = JsonMembers.Items(request, "permission_suggestions").Count > 0 ? JsonMembers.Value(request, "permission_suggestions")!.Value.GetRawText() : null;
        asked[id] = (input?.GetRawText() ?? "{}", always);
        events.Add(new AgentAsked(id, ToolTitle(JsonMembers.Text(request, "display_name") ?? tool), Cut(Detail(tool, input)), always is not null));
    }

    private void Ended(JsonElement message, List<AgentEvent> events)
    {
        working = false;
        foreach (string id in asked.Keys.ToList())
        {
            asked.Remove(id);
            events.Add(new AgentAskEnded(id));
        }
        foreach (string id in tools.Keys.ToList())
        {
            tools.Remove(id);
            events.Add(new AgentToolEnded(id, true, null, []));
        }
        blocks.Clear();
        streamed.Clear();
        bool stopped = stopping;
        stopping = false;
        string? why = JsonMembers.Text(message, "result") ?? string.Join('\n', JsonMembers.Texts(message, "errors"));
        events.Add(stopped ? new AgentTurnEnded(AgentTurnEnd.Stopped)
            : JsonMembers.Bool(message, "is_error") == true ? new AgentTurnEnded(AgentTurnEnd.Failed, string.IsNullOrWhiteSpace(why) ? JsonMembers.Text(message, "subtype") : why)
            : new AgentTurnEnded(AgentTurnEnd.Completed));
    }
}
