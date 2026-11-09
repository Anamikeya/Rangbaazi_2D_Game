using System;
using System.Threading.Tasks;

namespace Rangbaazi.Relay;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        RelayOptions options;
        try
        {
            options = RelayOptions.FromEnvironment(Environment.GetEnvironmentVariable);
        }
        catch (FormatException ex)
        {
            await Console.Error.WriteLineAsync("Invalid relay configuration: " + ex.Message);
            return 2;
        }

        await using var app = RelayApp.Build(options, args);
        await app.RunAsync();
        return 0;
    }
}
