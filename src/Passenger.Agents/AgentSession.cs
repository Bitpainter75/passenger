using System.Diagnostics;
using System.Threading.Channels;
using Passenger.Core;

namespace Passenger.Agents;

/// <summary>An agent's program running for one conversation: started in its folder as a child that ends with the IDE,
/// its protocol read by its <see cref="AgentWire"/>, the lines the wire answers with written back in turn. What the
/// conversation tells is heard on the thread pool, in order, <see cref="AgentEnded"/> last; what the person asks
/// (<see cref="Say"/>, <see cref="Answer"/>, <see cref="Stop"/>) may come from any thread and never waits for the
/// program to read it.</summary>
public sealed class AgentSession
{
    /// <summary>How many of the last lines the program wrote to its errors are told as it ends.</summary>
    private const int ErrorLines = 20;

    private readonly AgentWire wire;
    private readonly Process process;
    private readonly Action<AgentEvent> heard;
    private readonly Channel<string> written = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Queue<string> errors = new();

    /// <summary>Held while the wire is read or asked, and while what it told is heard, so its events keep their order.</summary>
    private readonly Lock turn = new();

    private AgentSession(AgentWire wire, Process process, Action<AgentEvent> heard)
    {
        this.wire = wire;
        this.process = process;
        this.heard = heard;
    }

    /// <summary>The program's process id.</summary>
    public int ProcessId { get; private init; }

    /// <summary>The task that ends once the program has, and <see cref="AgentEnded"/> was heard.</summary>
    public Task Ended { get; private set; } = Task.CompletedTask;

    /// <summary>Starts <paramref name="program"/> for a conversation. Throws what a start throws where it cannot run.</summary>
    public static AgentSession Start(AgentWire wire, string program, AgentStart start, Action<AgentEvent> heard)
    {
        ProcessStartInfo info = ChildProcesses.Command(program, wire.Arguments(start), start.Folder);
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        Process process = ChildProcesses.Start(info);
        AgentSession session = new(wire, process, heard) { ProcessId = process.Id };
        session.Write(wire.Begin(start));
        session.Ended = session.RunAsync();
        return session;
    }

    private async Task RunAsync()
    {
        // Unheld: it writes for as long as the conversation lasts, which no wait for the IDE to be quiet may wait out.
        Task writing = BackgroundWork.Unheld(WriteAsync);
        await ChildProcesses.Hear(process, Read, Kept).ConfigureAwait(false);
        written.Writer.TryComplete();
        await writing.ConfigureAwait(false);
        int? code = null;
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            code = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // Its process was let go of as it ended: it has no exit code to tell.
        }
        string said;
        lock (errors)
        {
            said = string.Join('\n', errors);
        }
        lock (turn)
        {
            heard(new AgentEnded(code, said));
        }
        process.Dispose();
    }

    private void Read(string line)
    {
        lock (turn)
        {
            AgentRead read = wire.Read(line);
            Write(read.Replies);
            foreach (AgentEvent told in read.Events)
            {
                heard(told);
            }
        }
    }

    private void Kept(string line)
    {
        lock (errors)
        {
            errors.Enqueue(line);
            if (errors.Count > ErrorLines)
            {
                errors.Dequeue();
            }
        }
    }

    private void Write(IReadOnlyList<string> lines)
    {
        foreach (string line in lines)
        {
            written.Writer.TryWrite(line);
        }
    }

    private async Task WriteAsync()
    {
        StreamWriter input = ChildProcesses.Input(process);
        try
        {
            await foreach (string line in written.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await input.WriteAsync(line.AsMemory()).ConfigureAwait(false);
                await input.WriteAsync('\n').ConfigureAwait(false);
                await input.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception gone) when (gone is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The program ended, or its input was closed, under a write: its end is told by its output's.
        }
    }

    /// <summary>What the person wrote, beginning a turn.</summary>
    public void Say(string prompt)
    {
        lock (turn)
        {
            Write(wire.Prompt(prompt));
        }
    }

    /// <summary>The person's answer to a question the agent asked.</summary>
    public void Answer(string id, AgentAnswer answer)
    {
        lock (turn)
        {
            Write(wire.Answer(id, answer));
        }
    }

    /// <summary>Stops the turn under way, the conversation staying.</summary>
    public void Stop()
    {
        lock (turn)
        {
            Write(wire.Interrupt());
        }
    }

    /// <summary>Ends the program: through its input, which each takes for the conversation's end, then with all it
    /// started should it still run. Done once it has ended.</summary>
    public async Task EndAsync()
    {
        written.Writer.TryComplete();
        await ChildProcesses.EndThroughInputAsync(process, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Ended.ConfigureAwait(false);
    }
}
