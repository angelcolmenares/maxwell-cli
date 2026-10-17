using Maxwell.Agents;
using Maxwell.Agents.Models;
using Maxwell.Agents.Work;

namespace Maxwell.Cli;

/// <summary>`maxwell-cli work` and `maxwell-cli tasks`.</summary>
internal static class WorkConsole
{
    /// <returns>0 if every task that ran succeeded (or there was nothing to do); 1 if any failed or the run was canceled.</returns>
    public static async Task<int> RunWorkAsync(MaxwellSessionService service, CliOptions options)
    {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            // First Ctrl+C: stop cleanly (the running task goes back to pending). Don't kill the process mid-write.
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        try
        {
            var runner = new WorkRunner(service);
            var summary = await runner.RunAsync(
                new WorkOptions { AgentName = options.AgentName, MaxTasks = options.MaxTasks },
                new ConsoleWorkObserver(),
                cts.Token);

            Console.WriteLine();
            if (summary.Done + summary.Failed == 0 && !summary.Canceled)
            {
                Console.WriteLine(options.AgentName is null
                    ? "No pending tasks."
                    : $"No pending tasks for {options.AgentName}.");
                return 0;
            }

            Console.WriteLine(
                $"Finished: {summary.Done} done, {summary.Failed} failed" +
                (summary.Remaining > 0 ? $", {summary.Remaining} still pending" : string.Empty) +
                (summary.Canceled ? " (canceled)" : string.Empty) + ".");

            if (summary.Remaining > 0 && !summary.Canceled)
            {
                Console.WriteLine($"Stopped at the --max-tasks limit ({options.MaxTasks}). Run `maxwell-cli work` again to continue.");
            }

            return summary.Failed > 0 || summary.Canceled ? 1 : 0;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    public static int ListTasks(MaxwellSessionService service)
    {
        var tasks = service.Tasks.Load(service.GetProjectId()).OrderBy(t => t.CreatedAt).ToList();
        if (tasks.Count == 0)
        {
            Console.WriteLine("No tasks on this project's board.");
            return 0;
        }

        foreach (var task in tasks)
        {
            Console.ForegroundColor = ColorFor(task.Status);
            Console.Write($"{task.Id}  {task.Status,-10}");
            Console.ResetColor();
            Console.WriteLine($" {task.CreatedBy} -> {task.AssignedAgent}: {Shorten(task.Description, 100)}");

            if (!string.IsNullOrWhiteSpace(task.Result))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    result: {Shorten(task.Result, 200)}");
                Console.ResetColor();
            }

            if (task.SessionId is not null)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    session: {task.SessionId}");
                Console.ResetColor();
            }
        }

        return 0;
    }

    private static ConsoleColor ColorFor(WorkItemStatus status) => status switch
    {
        WorkItemStatus.Done => ConsoleColor.Green,
        WorkItemStatus.Failed => ConsoleColor.Red,
        WorkItemStatus.InProgress => ConsoleColor.Yellow,
        _ => ConsoleColor.Gray,
    };

    private static string Shorten(string text, int max)
    {
        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length <= max ? singleLine : singleLine[..max] + "…";
    }

    private sealed class ConsoleWorkObserver : IWorkObserver
    {
        private bool _inReasoning;

        public void OnTaskStarted(WorkItem task)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"▶ {task.Id} → {task.AssignedAgent}: {Shorten(task.Description, 100)}");
            Console.ResetColor();
            Console.Write($"{task.AssignedAgent}> ");
            _inReasoning = false;
        }

        public void OnChunk(WorkItem task, StreamChunk chunk)
        {
            if (chunk.IsReasoning)
            {
                if (!_inReasoning)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("\n  (thinking) ");
                    _inReasoning = true;
                }

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(chunk.Text);
                return;
            }

            if (_inReasoning)
            {
                Console.ResetColor();
                Console.WriteLine();
                Console.Write($"{task.AssignedAgent}> ");
                _inReasoning = false;
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(chunk.Text);
        }

        public void OnTaskFinished(WorkItem task)
        {
            Console.ResetColor();
            Console.WriteLine();
            Console.ForegroundColor = ColorFor(task.Status);
            Console.WriteLine(task.Status == WorkItemStatus.Done ? $"✔ {task.Id} done" : $"✘ {task.Id} failed");
            Console.ResetColor();

            if (!string.IsNullOrWhiteSpace(task.Result))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  {Shorten(task.Result, 300)}");
                Console.ResetColor();
            }
        }
    }
}
