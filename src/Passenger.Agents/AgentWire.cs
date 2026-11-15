using System.Text.Json;
using System.Text.Json.Nodes;

namespace Passenger.Agents;

/// <summary>What reading a line of an agent's output came to: what it tells, and the lines to write back to it.</summary>
public readonly record struct AgentRead(IReadOnlyList<AgentEvent> Events, IReadOnlyList<string> Replies)
{
    public static AgentRead Nothing { get; } = new([], []);
}

/// <summary>One agent's protocol for one conversation, as text alone: the arguments its program takes, the lines to
/// write it (each a JSON object, the program's input a line each) and what its lines tell. It starts no program and
/// reads no clock, so a recorded conversation reads the same every time; <see cref="AgentSession"/> runs the program.
/// One caller at a time.</summary>
public abstract class AgentWire
{
    /// <summary>The longest a tool's detail or output is told: an agent reads whole files, a window shows their start.</summary>
    public const int Longest = 4000;

    /// <summary>The program's arguments for a conversation starting so.</summary>
    public abstract IReadOnlyList<string> Arguments(AgentStart start);

    /// <summary>The lines written first, before anything is read.</summary>
    public abstract IReadOnlyList<string> Begin(AgentStart start);

    /// <summary>Reads one line the program wrote; a line that is no JSON object tells nothing.</summary>
    public AgentRead Read(string line)
    {
        if (line.Length == 0 || line[0] != '{')
        {
            return AgentRead.Nothing;
        }
        JsonDocument read;
        try
        {
            read = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return AgentRead.Nothing;
        }
        using (read)
        {
            List<AgentEvent> events = [];
            List<string> replies = [];
            Read(read.RootElement, events, replies);
            return new AgentRead(events, replies);
        }
    }

    protected abstract void Read(JsonElement message, List<AgentEvent> events, List<string> replies);

    /// <summary>The lines that say what the person wrote, beginning a turn.</summary>
    public abstract IReadOnlyList<string> Prompt(string text);

    /// <summary>The lines that answer a question (<see cref="AgentAsked"/>) by its id; none for one no longer asked.</summary>
    public abstract IReadOnlyList<string> Answer(string id, AgentAnswer answer);

    /// <summary>The lines that stop the turn under way; none while there is none.</summary>
    public abstract IReadOnlyList<string> Interrupt();

    /// <summary>A text cut to <see cref="Longest"/>, saying how much more there was.</summary>
    protected static string Cut(string text) =>
        text.Length <= Longest ? text : $"{text[..Longest]}\n… {text.Length - Longest:N0} more characters";

    /// <summary>A JSON value as one line for a person to read: a string's own text, anything else as JSON writes it.</summary>
    protected static string Shown(JsonElement? value) => value switch
    {
        null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => string.Empty,
        { ValueKind: JsonValueKind.String } text => text.GetString()!,
        { } other => other.GetRawText(),
    };

    protected static string Line(JsonObject message) => message.ToJsonString(Written);

    /// <summary>JSON written on one line with a person's text as they wrote it, only what JSON must escape escaped.</summary>
    private static readonly JsonSerializerOptions Written = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
