using Passenger.Agents;
using Passenger.App.Agents;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>The Agent window: the person's conversations with the coding agents installed on the machine (Claude Code,
/// Codex), one an agent, each run by the agent's own program in the open folder (<see cref="AgentChat"/>) and given
/// this window as its MCP server, which it joins as any agent does: let in by the person, asked before a tool that
/// changes something. A conversation lasts while the window stays in its folder; its programs end as it leaves or
/// closes. The conversations are the window's, kept while the view is hidden; the view is built as it is first shown.</summary>
public partial class MainWindow
{
    private readonly List<AgentChat> agentChats = [.. AgentProgram.All.Select(p => new AgentChat(p))];
    private AgentChat? agentChosen;
    private AgentView? agentView;

    /// <summary>The Agent window's view, built as it is first shown or asked for.</summary>
    public AgentView AgentWindow => agentView ?? (AgentView)Dock[ToolWindowIds.Agent].Content;

    /// <summary>The conversation with each agent, in the order of the window's tabs.</summary>
    public IReadOnlyList<AgentChat> AgentChats => agentChats;

    /// <summary>The conversation the Agent window shows.</summary>
    public AgentChat AgentChat => agentChosen ??= agentChats[0];

    private static string AgentActionOf(AgentChat chat) => chat.Program.Kind == AgentKind.ClaudeCode ? A.AgentClaudeCode : A.AgentCodex;

    private void AddAgentWindow()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Agent, "Agent", IconKeys.Agent, () =>
        {
            AgentView view = agentView = new AgentView(agentChats, AgentChat, WhyNoAgent, AgentActionOf, action => Request(action), Browse, path => OpenFile(path), () => Folder);
            ActionButtons.ShowMarked(view, actions!);
            return view;
        }, Docking.ToolAnchor.RightTop)
        {
            Action = A.ShowAgent,
            Menus = [new MenuPlace("Tools", 0, 10)],
        });
        foreach (AgentChat chat in agentChats)
        {
            // A question waits for the person where they see it, the keys left where they are.
            chat.Asked += (_, _) =>
            {
                agentChosen = chat;
                Dock.ShowBehind(ToolWindowIds.Agent);
                AgentWindow.Show(chat);
            };
            actions!.Add(new IdeAction(AgentActionOf(chat), $"Agent: {chat.Program.Name}", "Agent", _ =>
            {
                agentChosen = chat;
                AgentWindow.Show(chat);
                Dock.Activate(ToolWindowIds.Agent);
            })
            {
                Words = [chat.Program.Command, "AI", "Chat", "Assistant"],
            });
        }
        actions!.Add(new IdeAction(A.AgentSend, "Send to Agent", "Agent", _ => SendToAgent())
        {
            WhyNot = _ => WhyNoAgent(AgentChat) ?? (AgentChat.Transcript.Working ? Refusals.AgentWorks(AgentChat.Program.Name)
                : string.IsNullOrWhiteSpace(agentView?.Written) ? Refusals.NoPrompt : null),
        });
        actions.Add(new IdeAction(A.AgentStop, "Stop Agent", "Agent", _ => AgentChat.Stop())
        {
            WhyNot = _ => AgentChat.Transcript.Working ? null : Refusals.AgentIdle(AgentChat.Program.Name),
        });
        actions.Add(new IdeAction(A.AgentNew, "New Agent Conversation", "Agent", _ => IdeErrors.Observe(AgentChat.RenewAsync()))
        {
            WhyNot = _ => AgentChat.Transcript.Entries.Count == 0 && !AgentChat.Runs ? Refusals.NoConversation(AgentChat.Program.Name) : null,
        });
        Closed += (_, _) => LeaveAgentChats();
        SizeChanged += (_, sized) => ShowWindowButtons(sized.NewSize.Width);
        ShowWindowButtons(Width);
    }

    /// <summary>The narrowest window whose toolbar shows the Agent and SQL Server windows' buttons: narrower, the names
    /// beside them would lose their room, and each window's stripe button still shows it.</summary>
    public const double WindowButtonsFrom = 960;

    private void ShowWindowButtons(double width)
    {
        bool room = double.IsNaN(width) || width >= WindowButtonsFrom;
        AgentToolButton.IsVisible = room;
        SqlServerToolButton.IsVisible = room;
        WindowButtonsRule.IsVisible = room;
    }

    /// <summary>Why the person cannot talk to an agent here, else null: no folder for it to work in, or no program of it.</summary>
    private string? WhyNoAgent(AgentChat chat) =>
        Folder is null ? Refusals.NoFolder : chat.Found is null ? Refusals.AgentNotInstalled(chat.Program.Name, chat.Program.Command) : null;

    private void SendToAgent()
    {
        AgentChat chat = AgentChat;
        string prompt = AgentWindow.Written.Trim();
        AgentWindow.Written = string.Empty;
        // The window as the agent's MCP server, where agents may reach it: it attaches to this process from the folder.
        AgentMcpServer? server = AgentsAllowed && AgentSocket is not null
            ? new AgentMcpServer(Mcp.AgentSetup.ThisExecutable(), ["--mcp-attach", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)])
            : null;
        chat.Say(prompt, new AgentStart(Folder!, server));
    }

    /// <summary>The window leaves its folder, or closes: each conversation ends with its program, as it was the folder's.</summary>
    private void LeaveAgentChats()
    {
        foreach (AgentChat chat in agentChats)
        {
            IdeErrors.Observe(chat.RenewAsync());
        }
        agentView?.Refresh();
    }
}
