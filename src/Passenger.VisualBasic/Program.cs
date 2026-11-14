namespace Passenger.VisualBasic;

/// <summary>Passenger's language server for Visual Basic, speaking the Language Server Protocol over its standard
/// streams until the client says exit or closes them.</summary>
public static class Program
{
    public static async Task<int> Main()
    {
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();
        Server server = new(new Wire(input, output));
        return await server.RunAsync().ConfigureAwait(false);
    }
}
