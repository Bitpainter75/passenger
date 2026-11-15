namespace Passenger.Agents;

/// <summary>What an agent's conversation tells, read from its protocol (<see cref="AgentWire"/>) into the few things a
/// window shows: words, tools at work, files changed, a question to the person, a turn's end.</summary>
public abstract record AgentEvent;

/// <summary>The conversation began: the model answering, where the agent names it.</summary>
public sealed record AgentStarted(string? Model) : AgentEvent;

/// <summary>More of a message the agent writes, as it comes; <see cref="Id"/> tells its messages apart.</summary>
public sealed record AgentWords(string Id, string More) : AgentEvent;

/// <summary>A message written to its end, whole: what <see cref="AgentWords"/> came to, or all of one never streamed.</summary>
public sealed record AgentMessage(string Id, string Text) : AgentEvent;

/// <summary>A tool begun: what it is called, what it works on (a command line, a file, its arguments), and the files it
/// is about to change where the agent says them first.</summary>
public sealed record AgentToolStarted(string Id, string Title, string Detail, IReadOnlyList<AgentFileEdit> Files) : AgentEvent;

/// <summary>A tool ended: whether it failed, what it wrote, and the files it changed where known only now.</summary>
public sealed record AgentToolEnded(string Id, bool Failed, string? Output, IReadOnlyList<AgentFileEdit> Files) : AgentEvent;

/// <summary>The agent waits for the person's leave before a tool runs: what for, in a title and its detail, and whether
/// the agent offers to stop asking about its like for the rest of the conversation.</summary>
public sealed record AgentAsked(string Id, string Title, string Detail, bool OffersAlways) : AgentEvent;

/// <summary>A question the agent no longer waits on: answered elsewhere, or its turn ended.</summary>
public sealed record AgentAskEnded(string Id) : AgentEvent;

/// <summary>A turn ended: completed, stopped by the person, or failed saying why.</summary>
public sealed record AgentTurnEnded(AgentTurnEnd How, string? Why = null) : AgentEvent;

/// <summary>Words of the agent's program beside the conversation: a warning, or an error it goes on after.</summary>
public sealed record AgentNotice(string Text, bool IsError) : AgentEvent;

/// <summary>The agent's program ended: its exit code where it has one, and the last it wrote to its errors.</summary>
public sealed record AgentEnded(int? ExitCode, string Errors) : AgentEvent;

public enum AgentTurnEnd
{
    Completed,
    Stopped,
    Failed,
}

/// <summary>The person's answer to <see cref="AgentAsked"/>.</summary>
public enum AgentAnswer
{
    Allow,
    AllowAlways,
    Deny,
}

/// <summary>How a file changes.</summary>
public enum AgentFileChange
{
    Added,
    Changed,
    Deleted,
}

/// <summary>A file an agent's tool changes: its path as the agent gives it, how, and the change as a unified diff's
/// lines (each beginning ' ', '+' or '-', hunks under their "@@" lines), empty where the agent gives none.</summary>
public sealed record AgentFileEdit(string Path, AgentFileChange Change, string Diff);
