using System.Text;
using Maxwell.Agents.Models;

namespace Maxwell.Agents.Work;

public sealed class WorkOptions
{
    /// <summary>Only run tasks assigned to this agent. Null runs everyone's.</summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// Stops after this many tasks. A guard against runaway chains (agent A's task
    /// creates a task for B, whose task creates another for A, ...): each task a
    /// worker creates is picked up in the same run, so without a cap that loop
    /// would never end.
    /// </summary>
    public int MaxTasks { get; init; } = 20;
}

/// <summary>Receives progress as <see cref="WorkRunner"/> runs. All methods are optional.</summary>
public interface IWorkObserver
{
    void OnTaskStarted(WorkItem task) { }

    void OnChunk(WorkItem task, StreamChunk chunk) { }

    /// <summary>Called with the task's final state (Done or Failed).</summary>
    void OnTaskFinished(WorkItem task) { }
}

/// <param name="Done">Tasks that ended Done.</param>
/// <param name="Failed">Tasks that ended Failed.</param>
/// <param name="Remaining">Pending tasks (matching the agent filter) still left - nonzero when the MaxTasks cap or a cancel stopped the run.</param>
/// <param name="Canceled">True if the run was canceled (e.g. Ctrl+C).</param>
public sealed record WorkSummary(int Done, int Failed, int Remaining, bool Canceled);

/// <summary>
/// `maxwell-cli work`: repeatedly takes the oldest pending task, runs its
/// assigned agent on it in a fresh session, and records the outcome.
///
/// Tasks run sequentially, never in parallel - a local llama.cpp server usually
/// can't serve several agents at once anyway, and it keeps tasks.json's
/// single-writer assumption true. The pending list is re-read after every task,
/// so tasks that workers create for each other during the run get picked up in
/// the same run (bounded by <see cref="WorkOptions.MaxTasks"/>).
///
/// The run is unattended: the agent is told there's no human to ask. Each task
/// must end with the agent calling complete_task or fail_task; if it doesn't, it
/// gets one nudge, and if it still doesn't, the task is marked Failed (with the
/// agent's last reply as context) rather than assumed done.
/// </summary>
public sealed class WorkRunner(MaxwellSessionService service)
{
    public async Task<WorkSummary> RunAsync(WorkOptions options, IWorkObserver observer, CancellationToken cancellationToken = default)
    {
        var projectId = service.GetProjectId();
        var tasks = service.Tasks;
        int done = 0, failed = 0, processed = 0;
        var canceled = false;

        while (processed < options.MaxTasks && !cancellationToken.IsCancellationRequested)
        {
            var next = PendingFor(options, projectId).FirstOrDefault();
            if (next is null)
            {
                break;
            }

            processed++;

            WorkItem finished;
            try
            {
                finished = await RunOneAsync(projectId, next, observer, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Put it back so the next `work` run retries it instead of leaving it stuck InProgress.
                tasks.Update(projectId, next.Id, t =>
                {
                    if (t.Status == WorkItemStatus.InProgress)
                    {
                        t.Status = WorkItemStatus.Pending;
                        t.StartedAt = null;
                    }
                });
                canceled = true;
                break;
            }

            if (finished.Status == WorkItemStatus.Done) done++; else failed++;
            observer.OnTaskFinished(finished);
        }

        return new WorkSummary(done, failed, PendingFor(options, projectId).Count(), canceled);
    }

    private IEnumerable<WorkItem> PendingFor(WorkOptions options, string projectId) =>
        service.Tasks.Load(projectId)
            .Where(t => t.Status == WorkItemStatus.Pending
                        && (options.AgentName is null
                            || string.Equals(t.AssignedAgent, options.AgentName, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(t => t.CreatedAt);

    private async Task<WorkItem> RunOneAsync(string projectId, WorkItem task, IWorkObserver observer, CancellationToken cancellationToken)
    {
        var tasks = service.Tasks;

        var started = tasks.Update(projectId, task.Id, t =>
        {
            t.Status = WorkItemStatus.InProgress;
            t.StartedAt = DateTimeOffset.UtcNow;
        }) ?? task;
        observer.OnTaskStarted(started);

        try
        {
            var session = await service.ResolveAsync(new SessionResolutionRequest
            {
                AgentName = task.AssignedAgent,
                IncludeTaskQueue = false,
            });
            tasks.Update(projectId, task.Id, t => t.SessionId = session.Record.SessionId);

            var reply = await StreamTurnAsync(session, started, BuildPrompt(started), observer, cancellationToken);

            var current = tasks.Find(projectId, task.Id) ?? started;
            if (current.Status == WorkItemStatus.InProgress)
            {
                // Small local models often finish the work and forget the bookkeeping call. One nudge is cheap.
                reply = await StreamTurnAsync(session, started, BuildNudge(started), observer, cancellationToken);
                current = tasks.Find(projectId, task.Id) ?? started;
            }

            if (current.Status == WorkItemStatus.InProgress)
            {
                current = tasks.Update(projectId, task.Id, t =>
                {
                    t.Status = WorkItemStatus.Failed;
                    t.CompletedAt = DateTimeOffset.UtcNow;
                    t.Result = "The agent ended without calling complete_task or fail_task. " +
                               $"Its last reply was: {Shorten(reply.Trim(), 1000)}";
                }) ?? current;
            }

            return current;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Includes HTTP timeouts (TaskCanceledException without our token being canceled): those are task failures, not a user cancel.
            return tasks.Update(projectId, task.Id, t =>
            {
                t.Status = WorkItemStatus.Failed;
                t.CompletedAt = DateTimeOffset.UtcNow;
                t.Result = $"Error while running the task: {ex.Message}";
            }) ?? started;
        }
    }

    private async Task<string> StreamTurnAsync(
        ActiveSession session, WorkItem task, string prompt, IWorkObserver observer, CancellationToken cancellationToken)
    {
        var reply = new StringBuilder();
        await foreach (var chunk in service.StreamAsync(session, prompt, cancellationToken))
        {
            if (!chunk.IsReasoning)
            {
                reply.Append(chunk.Text);
            }

            observer.OnChunk(task, chunk);
        }

        return reply.ToString();
    }

    private static string BuildPrompt(WorkItem task) =>
        $"""
        You have been assigned a task by {task.CreatedBy} (task id: {task.Id}):

        {task.Description}

        Do the work now. This run is unattended - there is no user to answer questions, so make reasonable
        assumptions and state them in your result instead of asking.

        When you are finished, call complete_task with taskId "{task.Id}" and a concise result: what you did
        and where any output is (file paths, key findings). If you cannot complete it, call fail_task with
        taskId "{task.Id}" and the reason.
        """;

    private static string BuildNudge(WorkItem task) =>
        $"You have not yet called complete_task or fail_task for {task.Id}. If the work is done, call " +
        $"complete_task now with taskId \"{task.Id}\" and a concise result. If you could not finish it, call fail_task with the reason.";

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
