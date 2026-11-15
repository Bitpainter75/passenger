using System.Text.Json;
using Passenger.Agents;

namespace Passenger.UnitTests.Agents;

/// <summary>The agents' protocols read as text: lines as Claude Code 2.1 and Codex 0.169 wrote them come to the
/// conversation's events, the lines written back are those each program takes, and a transcript of those events is what
/// the Agent window shows.</summary>
public class AgentWireTests
{
    private static readonly AgentStart Start = new("/work", new AgentMcpServer("/opt/Passenger", ["--mcp-attach", "42"]));

    private static List<AgentEvent> Told(AgentWire wire, List<string> replies, params string[] lines)
    {
        List<AgentEvent> told = [];
        foreach (string line in lines)
        {
            AgentRead read = wire.Read(line);
            told.AddRange(read.Events);
            replies.AddRange(read.Replies);
        }
        return told;
    }

    private static JsonElement Json(string line) => JsonDocument.Parse(line).RootElement;

    [Fact]
    public void Claude_Code_is_started_in_its_streaming_print_mode_asking_leave_over_its_input_with_Passenger_as_its_MCP_server()
    {
        IReadOnlyList<string> arguments = new ClaudeCodeWire().Arguments(Start);
        Assert.Equal(["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--permission-prompt-tool", "stdio", "--mcp-config"],
            arguments.Take(10));
        JsonElement server = Json(arguments[10]).GetProperty("mcpServers").GetProperty("passenger");
        Assert.Equal("/opt/Passenger", server.GetProperty("command").GetString());
        Assert.Equal(["--mcp-attach", "42"], server.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.DoesNotContain("--mcp-config", new ClaudeCodeWire().Arguments(new AgentStart("/work")));
    }

    [Fact]
    public void Claude_Codes_words_stream_into_one_message_and_its_tool_ends_with_the_file_it_changed()
    {
        ClaudeCodeWire wire = new();
        List<string> replies = [];
        JsonElement prompt = Json(Assert.Single(wire.Prompt("Say \"hi\"")));
        Assert.Equal("Say \"hi\"", prompt.GetProperty("message").GetProperty("content").GetString());
        List<AgentEvent> told = Told(wire, replies,
            """{"type":"system","subtype":"init","model":"claude-haiku-5-5"}""",
            """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"On "}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"it"}},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"On it"}]},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","id":"t1","name":"Edit","input":{"file_path":"/work/a.cs","old_string":"1","new_string":"2"}}]},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"id":"s1","content":[{"type":"text","text":"a subagent's own words"}]},"parent_tool_use_id":"t9"}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"done"}]},"parent_tool_use_id":null,"tool_use_result":{"filePath":"/work/a.cs","structuredPatch":[{"oldStart":3,"oldLines":1,"newStart":3,"newLines":1,"lines":["-1","+2"]}]}}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"On it"}""");
        Assert.Empty(replies);
        Assert.Equal(
        [
            new AgentStarted("claude-haiku-5-5"), new AgentWords("text-1", "On "), new AgentWords("text-1", "it"), new AgentMessage("text-1", "On it"),
        ], told.Take(4));
        AgentToolStarted started = Assert.IsType<AgentToolStarted>(told[4]);
        Assert.Equal(("t1", "Edit", "/work/a.cs"), (started.Id, started.Title, started.Detail));
        AgentToolEnded ended = Assert.IsType<AgentToolEnded>(told[5]);
        Assert.Equal(("t1", false, "done"), (ended.Id, ended.Failed, ended.Output));
        Assert.Equal(new AgentFileEdit("/work/a.cs", AgentFileChange.Changed, "@@ -3,1 +3,1 @@\n-1\n+2"), Assert.Single(ended.Files));
        Assert.Equal(new AgentTurnEnded(AgentTurnEnd.Completed), told[6]);
        Assert.Equal(7, told.Count);
    }

    [Fact]
    public void Claude_Codes_question_is_answered_with_the_tools_input_given_back_and_its_rules_kept_when_never_asked_again()
    {
        const string Asked = """{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"mcp__passenger__build","display_name":"mcp__passenger__build","input":{"command":"ls -la"},"permission_suggestions":[{"type":"addRules","behavior":"allow"}]}}""";
        ClaudeCodeWire wire = new();
        List<string> replies = [];
        wire.Prompt("go");
        Assert.Equal(new AgentAsked("r1", "passenger: build", "ls -la", OffersAlways: true), Assert.Single(Told(wire, replies, Asked)));
        JsonElement always = Json(Assert.Single(wire.Answer("r1", AgentAnswer.AllowAlways))).GetProperty("response");
        Assert.Equal(("success", "r1"), (always.GetProperty("subtype").GetString(), always.GetProperty("request_id").GetString()));
        Assert.Equal("allow", always.GetProperty("response").GetProperty("behavior").GetString());
        Assert.Equal("ls -la", always.GetProperty("response").GetProperty("updatedInput").GetProperty("command").GetString());
        Assert.Equal("addRules", always.GetProperty("response").GetProperty("updatedPermissions")[0].GetProperty("type").GetString());
        Assert.Empty(wire.Answer("r1", AgentAnswer.Allow));

        Told(wire, replies, Asked.Replace("r1", "r2", StringComparison.Ordinal));
        JsonElement denied = Json(Assert.Single(wire.Answer("r2", AgentAnswer.Deny))).GetProperty("response").GetProperty("response");
        Assert.Equal("deny", denied.GetProperty("behavior").GetString());
        Assert.False(denied.TryGetProperty("updatedInput", out _));

        // Its question form, which the window does not draw, is refused at once, so the turn goes on in words.
        Assert.Empty(Told(wire, replies, """{"type":"control_request","request_id":"r3","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":{"questions":[]}}}"""));
        Assert.Equal("deny", Json(Assert.Single(replies)).GetProperty("response").GetProperty("response").GetProperty("behavior").GetString());
    }

    [Fact]
    public void A_turn_of_Claude_Codes_the_person_stopped_ends_stopped_though_the_program_calls_it_failed()
    {
        ClaudeCodeWire wire = new();
        Assert.Empty(wire.Interrupt());
        wire.Prompt("go");
        Assert.Equal("interrupt", Json(Assert.Single(wire.Interrupt())).GetProperty("request").GetProperty("subtype").GetString());
        List<AgentEvent> told = Told(wire, [], """{"type":"result","subtype":"error_during_execution","is_error":true,"terminal_reason":"aborted_tools","errors":["[ede_diagnostic] x"]}""");
        Assert.Equal(new AgentTurnEnded(AgentTurnEnd.Stopped), Assert.Single(told));
        wire.Prompt("again");
        Assert.Equal(new AgentTurnEnded(AgentTurnEnd.Failed, "Credit balance is too low"),
            Assert.Single(Told(wire, [], """{"type":"result","subtype":"success","is_error":true,"result":"Credit balance is too low"}""")));
    }

    [Fact]
    public void Codex_starts_a_thread_in_the_folder_before_it_takes_the_prompt_written_meanwhile()
    {
        CodexWire wire = new();
        Assert.Equal(["app-server", "-c", "mcp_servers.passenger.command=\"/opt/Passenger\"", "-c", "mcp_servers.passenger.args=[\"--mcp-attach\",\"42\"]"], wire.Arguments(Start));
        JsonElement initialize = Json(Assert.Single(wire.Begin(Start)));
        Assert.Equal(("initialize", 1), (initialize.GetProperty("method").GetString(), initialize.GetProperty("id").GetInt32()));
        Assert.Empty(wire.Prompt("hello"));
        List<string> replies = [];
        Assert.Empty(Told(wire, replies, """{"id":1,"result":{"userAgent":"x"}}"""));
        Assert.Equal("initialized", Json(replies[0]).GetProperty("method").GetString());
        Assert.Equal(("thread/start", "/work"), (Json(replies[1]).GetProperty("method").GetString(), Json(replies[1]).GetProperty("params").GetProperty("cwd").GetString()));
        replies.Clear();
        Assert.Equal(new AgentStarted("gpt-5.6"), Assert.Single(Told(wire, replies, """{"id":2,"result":{"thread":{"id":"th1"},"model":"gpt-5.6"}}""")));
        JsonElement turn = Json(Assert.Single(replies)).GetProperty("params");
        Assert.Equal(("th1", "hello"), (turn.GetProperty("threadId").GetString(), turn.GetProperty("input")[0].GetProperty("text").GetString()));
    }

    private static CodexWire CodexInATurn()
    {
        CodexWire wire = new();
        wire.Begin(Start);
        Told(wire, [], """{"id":1,"result":{}}""", """{"id":2,"result":{"thread":{"id":"th1"},"model":"m"}}""");
        wire.Prompt("go");
        Told(wire, [], """{"id":3,"result":{"turn":{"id":"tu1"}}}""");
        return wire;
    }

    [Fact]
    public void Codexs_items_come_as_words_a_command_and_a_patch_whose_question_names_its_files()
    {
        CodexWire wire = CodexInATurn();
        List<string> replies = [];
        List<AgentEvent> told = Told(wire, replies,
            """{"method":"item/agentMessage/delta","params":{"threadId":"th1","turnId":"tu1","itemId":"m1","delta":"I"}}""",
            """{"method":"item/agentMessage/delta","params":{"threadId":"other","turnId":"x","itemId":"m9","delta":"a subagent's"}}""",
            """{"method":"item/completed","params":{"threadId":"th1","item":{"type":"agentMessage","id":"m1","text":"I will"}}}""",
            """{"method":"item/started","params":{"threadId":"th1","item":{"type":"commandExecution","id":"c1","command":"ls","status":"inProgress"}}}""",
            """{"method":"item/commandExecution/requestApproval","id":0,"params":{"threadId":"th1","itemId":"c1","command":"ls","reason":"needs the network"}}""",
            """{"method":"serverRequest/resolved","params":{"threadId":"th1","requestId":0}}""",
            """{"method":"item/completed","params":{"threadId":"th1","item":{"type":"commandExecution","id":"c1","command":"ls","status":"failed","aggregatedOutput":"no\n","exitCode":1}}}""",
            """{"method":"item/started","params":{"threadId":"th1","item":{"type":"fileChange","id":"p1","status":"inProgress","changes":[{"path":"/work/new.txt","kind":{"type":"add"},"diff":"hi\n"},{"path":"/work/a.cs","kind":{"type":"update","move_path":null},"diff":"--- a\n+++ b\n@@ -1 +1 @@\n-1\n+2\n"}]}}}""",
            """{"method":"item/fileChange/requestApproval","id":1,"params":{"threadId":"th1","itemId":"p1","reason":null}}""",
            """{"method":"item/tool/requestUserInput","id":2,"params":{"threadId":"th1","questions":[]}}""");
        Assert.Equal([new AgentWords("m1", "I"), new AgentMessage("m1", "I will")], told.Take(2));
        Assert.Equal(("c1", "Run", "ls"), told[2] is AgentToolStarted run ? (run.Id, run.Title, run.Detail) : default);
        Assert.Equal(new AgentAsked("ask-0", "Run", "ls\nneeds the network", OffersAlways: true), told[3]);
        Assert.Equal(new AgentAskEnded("ask-0"), told[4]);
        Assert.Equal(("c1", true, "no"), told[5] is AgentToolEnded ended ? (ended.Id, ended.Failed, ended.Output) : default);
        AgentToolStarted patch = Assert.IsType<AgentToolStarted>(told[6]);
        Assert.Equal([new AgentFileEdit("/work/new.txt", AgentFileChange.Added, "+hi"), new AgentFileEdit("/work/a.cs", AgentFileChange.Changed, "@@ -1 +1 @@\n-1\n+2")], patch.Files);
        Assert.Equal(new AgentAsked("ask-1", "Edit", "/work/new.txt\n/work/a.cs", OffersAlways: true), told[7]);
        Assert.Equal(8, told.Count);
        // A request it has no answer to is refused, so no turn waits on it.
        Assert.Equal(-32601, Json(Assert.Single(replies)).GetProperty("error").GetProperty("code").GetInt32());

        JsonElement answer = Json(Assert.Single(wire.Answer("ask-1", AgentAnswer.AllowAlways)));
        Assert.Equal((1, "acceptForSession"), (answer.GetProperty("id").GetInt32(), answer.GetProperty("result").GetProperty("decision").GetString()));
        Assert.Empty(wire.Answer("ask-0", AgentAnswer.Allow));
    }

    [Fact]
    public void Codexs_turn_is_stopped_by_its_id_and_ends_as_the_program_says()
    {
        CodexWire wire = CodexInATurn();
        JsonElement stop = Json(Assert.Single(wire.Interrupt()));
        Assert.Equal(("turn/interrupt", "th1", "tu1"),
            (stop.GetProperty("method").GetString(), stop.GetProperty("params").GetProperty("threadId").GetString(), stop.GetProperty("params").GetProperty("turnId").GetString()));
        Assert.Equal(new AgentTurnEnded(AgentTurnEnd.Stopped), Assert.Single(Told(wire, [], """{"method":"turn/completed","params":{"threadId":"th1","turn":{"id":"tu1","status":"interrupted"}}}""")));
        Assert.Empty(wire.Interrupt());

        // A stop asked before the next turn told its id goes to it as it does.
        wire.Prompt("again");
        Assert.Empty(wire.Interrupt());
        List<string> replies = [];
        Told(wire, replies, """{"method":"turn/started","params":{"threadId":"th1","turn":{"id":"tu2"}}}""");
        Assert.Equal("tu2", Json(Assert.Single(replies)).GetProperty("params").GetProperty("turnId").GetString());
        Assert.Equal(new AgentTurnEnded(AgentTurnEnd.Failed, "You are not signed in"),
            Assert.Single(Told(wire, [], """{"method":"turn/completed","params":{"threadId":"th1","turn":{"id":"tu2","status":"failed","error":{"message":"You are not signed in"}}}}""")));
    }

    [Fact]
    public void A_line_that_is_no_message_tells_nothing()
    {
        foreach (AgentWire wire in (AgentWire[])[new ClaudeCodeWire(), new CodexWire()])
        {
            Assert.All((string[])["", "warning: slow disk", "{broken", "[1,2]", "{\"type\":7}", "{\"method\":\"item/started\",\"params\":[]}"], line =>
            {
                AgentRead read = wire.Read(line);
                Assert.Empty(read.Events);
                Assert.Empty(read.Replies);
            });
        }
    }

    [Fact]
    public void A_transcript_grows_a_message_ends_its_tools_and_closes_its_questions_as_the_turn_does()
    {
        AgentTranscript transcript = new("Codex");
        List<string> seen = [];
        transcript.Added += e => seen.Add($"+{e.GetType().Name}");
        transcript.Changed += e => seen.Add($"~{e.GetType().Name}");
        transcript.Said("go");
        Assert.True(transcript.Working);
        transcript.Take(new AgentWords("m1", "He"));
        transcript.Take(new AgentWords("m1", "llo"));
        transcript.Take(new AgentToolStarted("t1", "Run", "ls", []));
        transcript.Take(new AgentAsked("a1", "Run", "ls", OffersAlways: false));
        transcript.Take(new AgentAsked("a2", "Run", "rm", OffersAlways: false));
        transcript.Answered("a1", AgentAnswer.Deny);
        transcript.Take(new AgentTurnEnded(AgentTurnEnd.Failed, "No network"));
        Assert.Equal(["+PersonEntry", "+WordsEntry", "~WordsEntry", "+ToolEntry", "+AskEntry", "+AskEntry", "~AskEntry", "~WordsEntry", "~ToolEntry", "~AskEntry", "+NoticeEntry"], seen);
        Assert.False(transcript.Working);
        WordsEntry words = Assert.IsType<WordsEntry>(transcript.Entries[1]);
        Assert.Equal(("Hello", true), (words.Text, words.Done));
        Assert.Equal(ToolState.Failed, Assert.IsType<ToolEntry>(transcript.Entries[2]).State);
        AskEntry denied = Assert.IsType<AskEntry>(transcript.Entries[3]);
        Assert.Equal((false, AgentAnswer.Deny), (denied.Open, denied.Answer));
        AskEntry unanswered = Assert.IsType<AskEntry>(transcript.Entries[4]);
        Assert.Equal((false, (AgentAnswer?)null), (unanswered.Open, unanswered.Answer));
        NoticeEntry notice = Assert.IsType<NoticeEntry>(transcript.Entries[5]);
        Assert.Equal(("No network", true), (notice.Text, notice.IsError));

        transcript.Take(new AgentEnded(3, "fatal: gone"));
        Assert.True(transcript.Ended);
        Assert.Equal("Codex ended with exit code 3\nfatal: gone", Assert.IsType<NoticeEntry>(transcript.Entries[^1]).Text);
        transcript.Said("still there?");
        Assert.False(transcript.Ended);
    }
}
