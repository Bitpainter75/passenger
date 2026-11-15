using System.ComponentModel;
using Passenger.Agents;

namespace Passenger.App.Agents;

/// <summary>A window's conversation with one agent: its transcript, and the agent's program, started in the folder at
/// the first prompt and kept until the conversation begins anew, the window leaves the folder or the program ends. What
/// the program tells comes from its own threads and lands here on the window's, in order; what a program let go of
/// still tells is passed over. The window's thread alone asks anything of it.</summary>
public sealed class AgentChat(AgentProgram program)
{
    private AgentSession? session;

    /// <summary>The conversation under way, by which a program let go of is told from the one running.</summary>
    private object? run;

    private string? found;

    public AgentProgram Program { get; } = program;

    public AgentTranscript Transcript { get; private set; } = new(program.Name);

    /// <summary>The program to start in place of the one on the PATH: a test's stand-in.</summary>
    public string? ProgramPath { get; set; }

    /// <summary>The agent's program, or null where it is not installed; found once, looked for again while missing.</summary>
    public string? Found => ProgramPath ?? (found ??= Program.Found());

    /// <summary>Whether its program runs.</summary>
    public bool Runs => session is not null;

    /// <summary>The transcript was replaced by an empty one (New Conversation, another folder).</summary>
    public event EventHandler? Renewed;

    /// <summary>The agent asked the person something, which waits in the transcript.</summary>
    public event EventHandler? Asked;

    /// <summary>Sends what the person wrote, starting the program first where none runs.</summary>
    public void Say(string prompt, AgentStart start)
    {
        UiThread.Check();
        AgentTranscript into = Transcript;
        into.Said(prompt);
        if (session is null)
        {
            object mine = run = new();
            try
            {
                session = AgentSession.Start(Program.Wire(), Found!, start, told => UiThread.Post(() => Take(mine, into, told)));
            }
            catch (Exception failed) when (failed is Win32Exception or InvalidOperationException or IOException)
            {
                run = null;
                into.Take(new AgentTurnEnded(AgentTurnEnd.Failed, $"{Program.Name} could not be started: {failed.Message}"));
                return;
            }
            IdeErrors.Observe(session.Ended);
        }
        session.Say(prompt);
    }

    private void Take(object from, AgentTranscript into, AgentEvent told)
    {
        if (from != run)
        {
            return;
        }
        if (told is AgentEnded)
        {
            session = null;
            run = null;
        }
        into.Take(told);
        if (told is AgentAsked)
        {
            Asked?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The person's answer to a question the agent waits on.</summary>
    public void Answer(AskEntry ask, AgentAnswer answer)
    {
        UiThread.Check();
        if (ask.Open)
        {
            session?.Answer(ask.Id, answer);
            Transcript.Answered(ask.Id, answer);
        }
    }

    /// <summary>Stops the turn under way.</summary>
    public void Stop()
    {
        UiThread.Check();
        session?.Stop();
    }

    /// <summary>Ends the program and begins with an empty transcript. Done once the program has ended.</summary>
    public Task RenewAsync()
    {
        UiThread.Check();
        AgentSession? ending = session;
        session = null;
        run = null;
        if (Transcript.Entries.Count > 0 || Transcript.Model is not null)
        {
            Transcript = new AgentTranscript(Program.Name);
            Renewed?.Invoke(this, EventArgs.Empty);
        }
        return ending?.EndAsync() ?? Task.CompletedTask;
    }
}
