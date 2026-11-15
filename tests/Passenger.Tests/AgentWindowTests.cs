using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.Agents;
using Passenger.App;
using Passenger.App.Agents;

namespace Passenger.Tests;

/// <summary>The Agent window, driven as a person drives it, against stand-ins that speak each agent's protocol as its
/// program does (a shell script, so these skip on Windows): what is written goes to the agent, its words, tools and
/// changed files show as they come, its question is answered by a button, and a conversation begun anew ends its
/// program. The protocols themselves are held by the unit tests (AgentWireTests).</summary>
public class AgentWindowTests
{
    private const string NoShell = "The stand-in agent is a shell script, which Windows does not run";

    private static string StandIn(string folder, string name, string script)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, "#!/bin/sh\n" + script.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    private static AgentChat Chosen(MainWindow window, AgentKind kind, string program)
    {
        AgentChat chat = window.AgentChats.Single(c => c.Program.Kind == kind);
        chat.ProgramPath = program;
        Assert.True(window.Perform(kind == AgentKind.ClaudeCode ? AppActions.AgentClaudeCode : AppActions.AgentCodex), $"The action that shows {chat.Program.Name} in the Agent window did not run");
        Ui.WaitFor(() => window.DockHost.IsShown(ToolWindowIds.Agent) && window.AgentWindow.Chat == chat, $"the Agent window on {chat.Program.Name}");
        return chat;
    }

    private static void Write(MainWindow window, string prompt)
    {
        window.KeyTextInput(prompt);
        Ui.WaitFor(() => window.AgentWindow.Written == prompt, () => $"'{prompt}' in the Agent window's box, which holds '{window.AgentWindow.Written}'");
        Ui.Press(window, "ENTER");
    }

    private static string Said(MainWindow window) => string.Join(" | ", window.AgentWindow.Shown);

    [AvaloniaFact]
    public void A_prompt_goes_to_Claude_Code_whose_words_tool_and_question_show_and_whose_question_a_button_answers()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), NoShell);
        string folder = TestFolders.Create("agent-claude-");
        string programs = TestFolders.Create("agent-bin-");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "one\n");
        string program = StandIn(programs, "claude", """
            read prompt
            echo '{"type":"system","subtype":"init","model":"stand-in"}'
            echo '{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"Reading **notes**"}]}}'
            echo '{"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"cat notes.txt"}}]}}'
            echo '{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"cat notes.txt"}}}'
            read answer
            case "$answer" in
              *'"behavior":"allow"'*'cat notes.txt'*) said=allowed ;;
              *) said=refused ;;
            esac
            echo '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"one"}]}}'
            echo '{"type":"assistant","message":{"id":"m2","content":[{"type":"text","text":"You '"$said"' it"}]}}'
            echo '{"type":"result","subtype":"success","is_error":false}'
            read rest
            """);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            AgentChat chat = Chosen(window, AgentKind.ClaudeCode, program);
            Assert.NotNull(window.AgentWindow.EmptyWords);
            Write(window, "What do my notes say?");
            Ui.WaitFor(() => window.AgentWindow.Answers.Count > 0, () => $"Claude Code's question in the Agent window, which shows: {Said(window)}");
            Assert.Equal(["You: What do my notes say?", "Claude Code: Reading **notes**", "Bash (Running): cat notes.txt", "Asks Bash: cat notes.txt"], window.AgentWindow.Shown);
            Assert.Equal(["Allow", "Don't Allow"], window.AgentWindow.Answers.Select(b => b.Content as string));
            Assert.Null(window.Actions.WhyNot(AppActions.AgentStop, window.Actions.Context()));

            Ui.Click(window.AgentWindow.Answers[0]);
            Ui.WaitFor(() => !chat.Transcript.Working, () => $"Claude Code's turn to end, which shows: {Said(window)}");
            Assert.Equal(["You: What do my notes say?", "Claude Code: Reading **notes**", "Bash (Done): cat notes.txt", "Asked Bash (Allowed): cat notes.txt", "Claude Code: You allowed it"],
                window.AgentWindow.Shown);
            Assert.Equal(Refusals.AgentIdle("Claude Code"), window.Actions.WhyNot(AppActions.AgentStop, window.Actions.Context()));

            Assert.True(window.Perform(AppActions.AgentNew), "New Agent Conversation did not run with a conversation there");
            Assert.Empty(window.AgentWindow.Shown);
            Ui.WaitFor(() => !chat.Runs, "Claude Code's program let go of as its conversation began anew");
            Assert.Equal(Refusals.NoConversation("Claude Code"), window.Actions.WhyNot(AppActions.AgentNew, window.Actions.Context()));
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
            TestFolders.Delete(programs);
        }
    }

    [AvaloniaFact]
    public void A_prompt_goes_to_Codex_once_its_thread_is_there_and_its_patch_shows_the_file_it_changed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), NoShell);
        string folder = TestFolders.Create("agent-codex-");
        string programs = TestFolders.Create("agent-bin-");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "one\n");
        string program = StandIn(programs, "codex", """
            read initialize
            echo '{"id":1,"result":{"userAgent":"stand-in"}}'
            read initialized
            read thread
            echo '{"id":2,"result":{"thread":{"id":"th1"},"model":"stand-in"}}'
            read turn
            echo '{"id":3,"result":{"turn":{"id":"tu1"}}}'
            case "$turn" in
              *'"threadId":"th1"'*'Add a line'*) said='On it' ;;
              *) said='No prompt came' ;;
            esac
            echo '{"method":"item/completed","params":{"threadId":"th1","item":{"type":"agentMessage","id":"m1","text":"'"$said"'"}}}'
            echo '{"method":"item/started","params":{"threadId":"th1","item":{"type":"fileChange","id":"p1","status":"inProgress","changes":[{"path":"'"$PWD"'/notes.txt","kind":{"type":"update","move_path":null},"diff":"@@ -1 +1,2 @@\n one\n+two\n"}]}}}'
            echo '{"method":"item/completed","params":{"threadId":"th1","item":{"type":"fileChange","id":"p1","status":"completed","changes":[{"path":"'"$PWD"'/notes.txt","kind":{"type":"update","move_path":null},"diff":"@@ -1 +1,2 @@\n one\n+two\n"}]}}}'
            echo '{"method":"turn/completed","params":{"threadId":"th1","turn":{"id":"tu1","status":"completed"}}}'
            read rest
            """);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            AgentChat chat = Chosen(window, AgentKind.Codex, program);
            Write(window, "Add a line");
            Ui.WaitFor(() => window.AgentWindow.Shown.Count >= 3 && !chat.Transcript.Working, () => $"Codex's turn to end, which shows: {Said(window)}");
            Assert.Equal(["You: Add a line", "Codex: On it", $"Edit (Done): {Path.Combine(window.Folder!, "notes.txt")}"], window.AgentWindow.Shown);
            ToolEntry patch = chat.Transcript.Entries.OfType<ToolEntry>().Single();
            Assert.Equal("@@ -1 +1,2 @@\n one\n+two", patch.Files.Single().Diff);
            // The other agent's conversation is its own, and this one is there again on its tab.
            Assert.True(window.Perform(AppActions.AgentClaudeCode));
            Assert.Empty(window.AgentWindow.Shown);
            Assert.True(window.Perform(AppActions.AgentCodex));
            Assert.Equal(3, window.AgentWindow.Shown.Count);
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
            TestFolders.Delete(programs);
        }
    }
}
