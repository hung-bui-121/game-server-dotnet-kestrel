namespace SampleGame.Tests;

/// <summary>
/// Captures what the sample host's entry point writes to <see cref="Console.Error"/> (the startup report of a refused start).
/// The console is process-wide, so tests that capture it run in <see cref="ConsoleErrorCollection"/>.
/// </summary>
internal static class ConsoleErrorCapture
{
    public static async Task<string> RunAsync(Func<Task> action)
    {
        var original = Console.Error;
        await using var writer = new StringWriter();
        Console.SetError(TextWriter.Synchronized(writer));
        try
        {
            await action();
        }
        finally
        {
            Console.SetError(original);
        }

        return writer.ToString();
    }
}

/// <summary>Tests that redirect <see cref="Console.Error"/> run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleErrorCollection
{
    public const string Name = "Console error capture";
}
