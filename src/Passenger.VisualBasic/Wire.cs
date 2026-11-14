using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Passenger.VisualBasic;

/// <summary>The Language Server Protocol's messages over two streams: each a JSON object after a
/// <c>Content-Length</c> header, read one at a time by the server's loop and written whole, one at a time, by whichever
/// thread answers.</summary>
internal sealed class Wire(Stream input, Stream output)
{
    /// <summary>The longest message taken: a document sent whole is a few megabytes at most.</summary>
    private const int Longest = 256 * 1024 * 1024;

    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly byte[] one = new byte[1];

    /// <summary>The next message, or null once the client closed its end or sent what is no message.</summary>
    public async Task<JsonObject?> ReadAsync(CancellationToken cancel)
    {
        int length = -1;
        StringBuilder header = new();
        while (true)
        {
            if (await input.ReadAsync(one, cancel).ConfigureAwait(false) == 0)
            {
                return null;
            }
            if (one[0] != (byte)'\n')
            {
                header.Append((char)one[0]);
                continue;
            }
            string line = header.ToString().TrimEnd('\r');
            header.Clear();
            if (line.Length == 0)
            {
                break;
            }
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line.AsSpan("Content-Length:".Length).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int said))
            {
                length = said;
            }
        }
        if (length is < 0 or > Longest)
        {
            return null;
        }
        byte[] body = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await input.ReadExactlyAsync(body.AsMemory(0, length), cancel).ConfigureAwait(false);
            return JsonNode.Parse(body.AsSpan(0, length)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
        }
    }

    public async Task WriteAsync(JsonObject message)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message);
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
        await writing.WaitAsync().ConfigureAwait(false);
        try
        {
            await output.WriteAsync(header).ConfigureAwait(false);
            await output.WriteAsync(body).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            writing.Release();
        }
    }
}
