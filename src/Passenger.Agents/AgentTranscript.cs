using System.Text;

namespace Passenger.Agents;

/// <summary>Something a conversation shows, in its order.</summary>
public abstract class AgentEntry;

/// <summary>What the person wrote.</summary>
public sealed class PersonEntry(string text) : AgentEntry
{
    public string Text { get; } = text;
}

/// <summary>A message of the agent's: its words so far, and whether it has written it to its end.</summary>
public sealed class WordsEntry : AgentEntry
{
    private readonly StringBuilder words = new();

    public string Text => words.ToString();

    public bool Done { get; private set; }

    internal void More(string more) => words.Append(more);

    internal void Whole(string text)
    {
        words.Clear().Append(text);
        Done = true;
    }

    internal void End() => Done = true;
}

public enum ToolState
{
    Running,
    Done,
    Failed,
}

/// <summary>A tool the agent used: what it is called and worked on, how it stands, what it wrote, the files it changed.</summary>
public sealed class ToolEntry(string title, string detail, IReadOnlyList<AgentFileEdit> files) : AgentEntry
{
    public string Title { get; } = title;

    public string Detail { get; } = detail;

    public ToolState State { get; internal set; }

    public string? Output { get; internal set; }

    public IReadOnlyList<AgentFileEdit> Files { get; internal set; } = files;
}

/// <summary>The agent's question whether a tool may run: open until answered or taken away, then saying which.</summary>
public sealed class AskEntry(string id, string title, string detail, bool offersAlways) : AgentEntry
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    public string Detail { get; } = detail;

    public bool OffersAlways { get; } = offersAlways;

    /// <summary>Whether the agent still waits on it.</summary>
    public bool Open { get; internal set; } = true;

    /// <summary>The person's answer, or null: not yet given, or the agent stopped waiting first.</summary>
    public AgentAnswer? Answer { get; internal set; }
}

/// <summary>Words beside the conversation: a warning, a turn stopped or failed, the program's end.</summary>
public sealed class NoticeEntry(string text, bool isError) : AgentEntry
{
    public string Text { get; } = text;

    public bool IsError { get; } = isError;
}

/// <summary>A conversation as a window shows it: what the person wrote and what the agent's events
/// (<see cref="AgentEvent"/>) came to, each entry in its order, a message growing as its words come, a tool going from
/// running to done, a question from open to answered. Its owner's thread alone reads and changes it.</summary>
public sealed class AgentTranscript(string agent)
{
    private readonly List<AgentEntry> entries = [];
    private readonly Dictionary<string, WordsEntry> words = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolEntry> tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AskEntry> asks = new(StringComparer.Ordinal);

    public IReadOnlyList<AgentEntry> Entries => entries;

    /// <summary>Whether a turn is under way: the agent works, or waits on a question.</summary>
    public bool Working { get; private set; }

    /// <summary>The model answering, once the agent named it.</summary>
    public string? Model { get; private set; }

    /// <summary>Whether the agent's program has ended, the conversation with it.</summary>
    public bool Ended { get; private set; }

    /// <summary>The questions the agent waits on.</summary>
    public IEnumerable<AskEntry> Asking => asks.Values.Where(a => a.Open);

    /// <summary>An entry came, last.</summary>
    public event Action<AgentEntry>? Added;

    /// <summary>An entry changed: more words, a tool's end, a question's answer.</summary>
    public event Action<AgentEntry>? Changed;

    /// <summary><see cref="Working"/>, <see cref="Model"/> or <see cref="Ended"/> changed.</summary>
    public event Action? StateChanged;

    private void Add(AgentEntry entry)
    {
        entries.Add(entry);
        Added?.Invoke(entry);
    }

    /// <summary>The person wrote a prompt: a turn begins, of a program started anew where the last one ended.</summary>
    public void Said(string prompt)
    {
        Add(new PersonEntry(prompt));
        Ended = false;
        Working = true;
        StateChanged?.Invoke();
    }

    /// <summary>The person answered a question, which closes it.</summary>
    public void Answered(string id, AgentAnswer answer)
    {
        if (asks.TryGetValue(id, out AskEntry? ask) && ask.Open)
        {
            ask.Open = false;
            ask.Answer = answer;
            Changed?.Invoke(ask);
        }
    }

    /// <summary>Takes what the agent told.</summary>
    public void Take(AgentEvent told)
    {
        switch (told)
        {
            case AgentStarted started:
                Model = started.Model;
                StateChanged?.Invoke();
                break;
            case AgentWords more:
                Message(more.Id, out bool cameNow).More(more.More);
                Tell(words[more.Id], cameNow);
                break;
            case AgentMessage message:
                Message(message.Id, out bool came).Whole(message.Text);
                Tell(words[message.Id], came);
                break;
            case AgentToolStarted started:
                Add(tools[started.Id] = new ToolEntry(started.Title, started.Detail, started.Files));
                break;
            case AgentToolEnded ended when tools.Remove(ended.Id, out ToolEntry? tool):
                tool.State = ended.Failed ? ToolState.Failed : ToolState.Done;
                tool.Output = string.IsNullOrEmpty(ended.Output) ? null : ended.Output;
                tool.Files = ended.Files.Count > 0 ? ended.Files : tool.Files;
                Changed?.Invoke(tool);
                break;
            case AgentAsked asked:
                Add(asks[asked.Id] = new AskEntry(asked.Id, asked.Title, asked.Detail, asked.OffersAlways));
                break;
            case AgentAskEnded ended when asks.TryGetValue(ended.Id, out AskEntry? ask) && ask.Open:
                ask.Open = false;
                Changed?.Invoke(ask);
                break;
            case AgentTurnEnded ended:
                TurnOver();
                if (ended.How != AgentTurnEnd.Completed)
                {
                    Add(new NoticeEntry(ended.How == AgentTurnEnd.Stopped ? "Stopped" : ended.Why ?? $"{agent} could not finish the turn", ended.How == AgentTurnEnd.Failed));
                }
                break;
            case AgentNotice notice:
                Add(new NoticeEntry(notice.Text, notice.IsError));
                break;
            case AgentEnded ended:
                Ended = true;
                TurnOver();
                if (ended.ExitCode is not (0 or null) || ended.Errors.Length > 0)
                {
                    string how = ended.ExitCode is { } code and not 0 ? $"{agent} ended with exit code {code}" : $"{agent} ended";
                    Add(new NoticeEntry(ended.Errors.Length > 0 ? $"{how}\n{ended.Errors}" : how, ended.ExitCode is not (0 or null)));
                }
                break;
        }
    }

    private WordsEntry Message(string id, out bool came)
    {
        came = !words.TryGetValue(id, out WordsEntry? entry);
        return entry ?? (words[id] = new WordsEntry());
    }

    /// <summary>A message told as it changed: added after its first words are in it, so what is added is never empty.</summary>
    private void Tell(WordsEntry entry, bool came)
    {
        if (came)
        {
            Add(entry);
        }
        else
        {
            Changed?.Invoke(entry);
        }
    }

    /// <summary>A turn is over: nothing of it is still written, run or asked.</summary>
    private void TurnOver()
    {
        foreach (WordsEntry entry in words.Values.Where(w => !w.Done))
        {
            entry.End();
            Changed?.Invoke(entry);
        }
        words.Clear();
        foreach (ToolEntry tool in tools.Values)
        {
            tool.State = ToolState.Failed;
            Changed?.Invoke(tool);
        }
        tools.Clear();
        foreach (AskEntry ask in asks.Values.Where(a => a.Open))
        {
            ask.Open = false;
            Changed?.Invoke(ask);
        }
        asks.Clear();
        Working = false;
        StateChanged?.Invoke();
    }
}
