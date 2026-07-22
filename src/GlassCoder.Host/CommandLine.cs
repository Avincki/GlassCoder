using System.Globalization;

namespace GlassCoder.Host;

/// <summary>What the host was asked to do.</summary>
public sealed record HostCommand
{
    /// <summary>The verb: <c>run</c>, <c>suite</c>, <c>ablate</c>, <c>tools</c> or <c>help</c>.</summary>
    public string Verb { get; init; } = "help";

    /// <summary>Extra configuration file layered over appsettings.json - this selects an arm.</summary>
    public string? ConfigPath { get; init; }

    /// <summary>Repository the agent works on. Defaults to the working directory.</summary>
    public string? RepoRoot { get; init; }

    /// <summary>The goal, for <c>run</c>.</summary>
    public string? Goal { get; init; }

    /// <summary>Task identifier, for metrics and transcripts.</summary>
    public string? TaskId { get; init; }

    /// <summary>Suite task to run, or null for all of them.</summary>
    public string? SuiteTask { get; init; }

    /// <summary>Directory to materialise suite fixtures into.</summary>
    public string? WorkDirectory { get; init; }

    /// <summary>Whether parse succeeded.</summary>
    public string? Error { get; init; }
}

/// <summary>Parses the host's command line. Deliberately tiny - no dependency, no surprises.</summary>
public static class CommandLine
{
    /// <summary>Usage text.</summary>
    public const string Usage = """
        glasscoder - a local AI coding agent harness

        USAGE
          glasscoder run    --goal "<goal>" [--task <id>] [options]
          glasscoder suite  [--suite-task <id>] [--work <dir>] [options]
          glasscoder ablate [--work <dir>] [options]
          glasscoder fixtures [--work <dir>] [options]
          glasscoder tools
          glasscoder help

        OPTIONS
          --config <path>   Configuration file layered over appsettings.json. This is how an
                            ablation arm is selected: one file, no code change.
          --repo <path>     Repository the agent works on. Defaults to the working directory.
          --task <id>       Task identifier recorded in metrics and transcripts.
          --suite-task <id> Run one suite task instead of all of them.
          --work <dir>      Where suite fixtures are materialised.

        EXIT CODES
          0  success                 3  a limit stopped the run
          1  the task did not pass   4  the model endpoint failed
          2  bad configuration       5  cancelled
        """;

    /// <summary>Parses arguments into a command.</summary>
    public static HostCommand Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            return new HostCommand { Verb = "help" };
        }

        string verb = args[0].TrimStart('-').ToLowerInvariant();
        Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                return new HostCommand { Verb = verb, Error = $"Unexpected argument '{args[i]}'." };
            }

            string name = args[i][2..];
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[name] = "true";
                continue;
            }

            options[name] = args[++i];
        }

        HostCommand command = new()
        {
            Verb = verb,
            ConfigPath = Value(options, "config"),
            RepoRoot = Value(options, "repo"),
            Goal = Value(options, "goal"),
            TaskId = Value(options, "task"),
            SuiteTask = Value(options, "suite-task"),
            WorkDirectory = Value(options, "work"),
        };

        return verb switch
        {
            "run" when string.IsNullOrWhiteSpace(command.Goal) =>
                command with { Error = "run needs --goal." },
            "run" or "suite" or "ablate" or "tools" or "fixtures" or "help" => command,
            _ => command with { Error = $"Unknown command '{verb}'." },
        };
    }

    /// <summary>Formats a duration for the console.</summary>
    public static string Duration(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 90
            ? elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s"
            : elapsed.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture) + "m";

    private static string? Value(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? value) ? value : null;
}
