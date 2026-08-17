using Maxwell.Agents;
using Maxwell.Cli;

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

// HomeDirectory = the user's home; WorkingDirectory = wherever maxwell-cli was launched from.
var paths = new MaxwellPaths();
var service = new MaxwellSessionService(paths);

try
{
    var session = await service.ResolveAsync(new SessionResolutionRequest
    {
        SessionId = options.SessionId,
        AgentName = options.AgentName,
    });

    await ChatConsole.RunAsync(service, session);
    return 0;
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"maxwell-cli: {ex.Message}");
    Console.ResetColor();
    return 1;
}
