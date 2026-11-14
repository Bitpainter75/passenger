namespace Passenger.VisualBasic;

/// <summary>Passenger's language server for Visual Basic, speaking the Language Server Protocol over its standard
/// streams until the client says exit or closes them.</summary>
public static class Program
{
    public static async Task<int> Main()
    {
        // Passenger is in English: the compiler's and the analyzers' words too, whatever the machine speaks.
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en");
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en");
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();
        Server server = new(new Wire(input, output));
        return await server.RunAsync().ConfigureAwait(false);
    }
}
