namespace Maxwell.Cli;

public enum CliCommand
{
    Chat,
    Work,
    Tasks,
}

/// <summary>
/// Parses:
///   maxwell-cli
///   maxwell-cli --session 121313
///   maxwell-cli --session 121313 --agent MyAgent
///   maxwell-cli --agent MyAgent
///   maxwell-cli work [--agent MyAgent] [--max-tasks 20]
///   maxwell-cli tasks
/// </summary>
public sealed class CliOptions
{
    private const int DefaultMaxTasks = 20;

    public CliCommand Command { get; private init; } = CliCommand.Chat;
    public string? SessionId { get; private init; }
    public string? AgentName { get; private init; }
    public int MaxTasks { get; private init; } = DefaultMaxTasks;

    public static CliOptions Parse(string[] args)
    {
        var command = CliCommand.Chat;
        var start = 0;

        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            command = args[0] switch
            {
                "work" => CliCommand.Work,
                "tasks" => CliCommand.Tasks,
                _ => throw new ArgumentException($"Unknown command '{args[0]}'. Run with --help for usage."),
            };
            start = 1;
        }

        string? sessionId = null;
        string? agentName = null;
        int? maxTasks = null;

        for (var i = start; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--session":
                case "-s":
                    sessionId = RequireValue(args, ref i, "--session");
                    break;

                case "--agent":
                case "-a":
                    agentName = RequireValue(args, ref i, "--agent");
                    break;

                case "--max-tasks":
                case "-n":
                    var raw = RequireValue(args, ref i, "--max-tasks");
                    if (!int.TryParse(raw, out var parsed) || parsed < 1)
                    {
                        throw new ArgumentException($"--max-tasks must be a positive whole number, got '{raw}'.");
                    }

                    maxTasks = parsed;
                    break;

                case "--help":
                case "-h":
                    PrintUsage();
                    Environment.Exit(0);
                    break;

                default:
                    throw new ArgumentException($"Unrecognized argument '{args[i]}'. Run with --help for usage.");
            }
        }

        if (command != CliCommand.Chat && sessionId is not null)
        {
            throw new ArgumentException("--session only applies to chat, not to 'work' or 'tasks'.");
        }

        if (command != CliCommand.Work && maxTasks is not null)
        {
            throw new ArgumentException("--max-tasks only applies to 'work'.");
        }

        if (command == CliCommand.Tasks && agentName is not null)
        {
            throw new ArgumentException("'tasks' doesn't take --agent.");
        }

        return new CliOptions
        {
            Command = command,
            SessionId = sessionId,
            AgentName = agentName,
            MaxTasks = maxTasks ?? DefaultMaxTasks,
        };
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {flag}.");
        }

        i++;
        return args[i];
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: maxwell-cli [--session <id>] [--agent <name>]
                   maxwell-cli work [--agent <name>] [--max-tasks <n>]
                   maxwell-cli tasks

              (no args)                      Start a new session with the default agent (Maxwell).
              --session <id>                 Continue session <id> with the agent it was created with.
              --session <id> --agent <name>  Continue session <id> if <name> matches its agent, otherwise
                                              branch a new session from it using <name>.
              --agent <name>                 Start a new session with agent <name>.

              work                           Run this project's pending tasks unattended, oldest first,
                                              one fresh session per task, then print a summary.
                                              Exits 1 if any task failed.
              work --agent <name>            Only run tasks assigned to <name>.
              work --max-tasks <n>           Stop after <n> tasks (default 20) - a guard against agents
                                              creating tasks for each other forever.
              tasks                          List this project's task board.
            """);
    }
}
