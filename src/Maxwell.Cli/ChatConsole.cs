using Maxwell.Agents;
using Microsoft.Extensions.AI;

namespace Maxwell.Cli;

internal static class ChatConsole
{
    public static async Task RunAsync(MaxwellSessionService service, ActiveSession session)
    {
        PrintBanner(session);

        while (true)
        {
            Console.Write("you> ");
            var input = Console.ReadLine();

            if (input is null)
            {
                // Ctrl+D / stdin closed.
                break;
            }

            var trimmed = input.Trim();
            if (trimmed is "exit" or "quit" or ":q")
            {
                break;
            }

            if (trimmed.Length == 0)
            {
                continue;
            }

            Console.Write($"{session.Record.Agent}> ");

            var inReasoning = false;
            try
            {
                await foreach (var chunk in service.StreamAsync(session, trimmed))
                {
                    if (chunk.IsReasoning)
                    {
                        if (!inReasoning)
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.Write("\n  (thinking) ");
                            inReasoning = true;
                        }

                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write(chunk.Text);
                    }
                    else
                    {
                        if (inReasoning)
                        {
                            // Switched from reasoning back to the actual answer:
                            // start a fresh, normally-colored line for it.
                            Console.ResetColor();
                            Console.WriteLine();
                            Console.Write($"{session.Record.Agent}> ");
                            inReasoning = false;
                        }

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.Write(chunk.Text);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine();
                Console.WriteLine($"error> {ex.Message}");
            }
            finally
            {
                Console.ResetColor();
            }

            Console.WriteLine();
            PrintUsage(session);
            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine($"Saved. Resume with: maxwell-cli --session {session.Record.SessionId}");
    }

    private static void PrintBanner(ActiveSession session)
    {
        Console.WriteLine("Maxwell CLI");
        Console.WriteLine($"  agent:   {session.Record.Agent}");
        Console.WriteLine($"  session: {session.Record.SessionId}{(session.IsNewSession ? " (new)" : "")}");
        if (session.Record.BranchedFromSessionId is { } from)
        {
            Console.WriteLine($"  branched from: {from}");
        }
        Console.WriteLine("Type 'exit' to end the chat.");
        Console.WriteLine();

        foreach (var turn in session.Record.Turns)
        {
            var label = turn.Role == "user" ? "you" : session.Record.Agent;
            Console.WriteLine($"{label}> {turn.Content}");
        }

        if (session.Record.Turns.Count > 0)
        {
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Prints token usage for the reply that was just streamed, plus a running
    /// session total. StreamAsync appends the assistant's ChatTurnRecord (with its
    /// Usage) before it finishes, so by the time the `await foreach` in the caller
    /// completes, session.Record.Turns[^1] is that turn.
    /// </summary>
    private static void PrintUsage(ActiveSession session)
    {
        if (session.Record.Turns.Count == 0 || session.Record.Turns[^1].Role != "assistant")
        {
            return;
        }

        var turnUsage = session.Record.Turns[^1].Usage;
        if (turnUsage is null)
        {
            return;
        }

        var cumulative = session.Record.CumulativeUsage;

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(
            $"[tokens] this turn — in: {Format(turnUsage.InputTokenCount)}, " +
            $"out: {Format(turnUsage.OutputTokenCount)}, " +
            $"total: {Format(turnUsage.TotalTokenCount)}" +
            // For a stateful chat, this turn's input already includes the whole
            // prior conversation, so it's a reasonable stand-in for "how full is
            // the context window right now" (exact only if the model doesn't
            // trim/summarize older turns itself).
            $"  |  context so far (≈): {Format(turnUsage.InputTokenCount)}");

        if (cumulative is not null)
        {
            Console.WriteLine(
                $"[tokens] session total — in: {Format(cumulative.InputTokenCount)}, " +
                $"out: {Format(cumulative.OutputTokenCount)}, " +
                $"total: {Format(cumulative.TotalTokenCount)}");
        }

        var speed = session.Record.Turns[^1].Speed;
        if (speed is not null)
        {
            var ttft = speed.TimeToFirstTokenSeconds is { } t ? $"{t:0.00}s" : "n/a";
            Console.WriteLine(
                $"[speed] {speed.OutputTokenCount} tokens in {speed.TotalDurationSeconds:0.00}s " +
                $"→ {speed.TokensPerSecond:0.0} tok/s (first token: {ttft})");
        }

        Console.ResetColor();
    }

    private static string Format(long? value) => value?.ToString() ?? "?";
}
