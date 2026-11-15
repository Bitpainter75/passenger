using Passenger.Core;

namespace Passenger.Agents;

/// <summary>The agents Passenger has a client for.</summary>
public enum AgentKind
{
    ClaudeCode,
    Codex,
}

/// <summary>An agent's command-line program as the person installed it: its name as its maker writes it, and the command
/// a terminal finds it by. Passenger carries none of them: it speaks to the one on the person's PATH, signed in as they
/// signed it in.</summary>
public sealed record AgentProgram(AgentKind Kind, string Name, string Command)
{
    public static AgentProgram ClaudeCode { get; } = new(AgentKind.ClaudeCode, "Claude Code", "claude");

    public static AgentProgram Codex { get; } = new(AgentKind.Codex, "Codex", "codex");

    /// <summary>Every agent, in the order the window offers them.</summary>
    public static IReadOnlyList<AgentProgram> All { get; } = [ClaudeCode, Codex];

    /// <summary>The program on the PATH, or null where it is not installed: on Windows its .exe, else npm's .cmd.</summary>
    public string? Found() => OperatingSystem.IsWindows()
        ? Programs.OnPath(Command + ".exe") ?? Programs.OnPath(Command + ".cmd")
        : Programs.OnPath(Command);

    /// <summary>A reader of this agent's protocol, for one conversation.</summary>
    public AgentWire Wire() => Kind switch
    {
        AgentKind.ClaudeCode => new ClaudeCodeWire(),
        _ => new CodexWire(),
    };
}

/// <summary>What a conversation starts with: the folder the agent works in, and this Passenger as the MCP server it is
/// given under the name every setup of Passenger's uses, so its tools reach the window the conversation is in.</summary>
public sealed record AgentStart(string Folder, AgentMcpServer? Passenger = null)
{
    /// <summary>The name Passenger's MCP server goes by in an agent's configuration.</summary>
    public const string ServerName = "passenger";
}

/// <summary>An MCP server an agent starts over standard input and output: its program and arguments.</summary>
public sealed record AgentMcpServer(string Command, IReadOnlyList<string> Arguments);
