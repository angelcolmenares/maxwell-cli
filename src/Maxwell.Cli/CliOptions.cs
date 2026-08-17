namespace Maxwell.Cli;

/// <summary>
/// Parses:
///   maxwell-cli
///   maxwell-cli --session 121313
///   maxwell-cli --session 121313 --agent MyAgent
///   maxwell-cli --agent MyAgent
/// </summary>
public sealed class CliOptions
{
    public string? SessionId { get; private init; }
    public string? AgentName { get; private init; }

    public static CliOptions Parse(string[] args)
    {
        string? sessionId = null;
        string? agentName = null;

        for (var i = 0; i < args.Length; i++)
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

                case "--help":
                case "-h":
                    PrintUsage();
                    Environment.Exit(0);
                    break;

                default:
                    throw new ArgumentException($"Unrecognized argument '{args[i]}'. Run with --help for usage.");
            }
        }

        return new CliOptions { SessionId = sessionId, AgentName = agentName };
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

              (no args)                      Start a new session with the default agent (Maxwell).
              --session <id>                 Continue session <id> with the agent it was created with.
              --session <id> --agent <name>  Continue session <id> if <name> matches its agent, otherwise
                                              branch a new session from it using <name>.
              --agent <name>                 Start a new session with agent <name>.
            """);
    }
}
