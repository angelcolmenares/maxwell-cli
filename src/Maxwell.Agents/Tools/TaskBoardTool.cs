using System.ComponentModel;
using System.Text;
using Maxwell.Agents.Models;
using Maxwell.Agents.Storage;

namespace Maxwell.Agents.Tools;

/// <summary>
/// The agent-facing side of the project's task board: create_task, list_tasks,
/// claim_task, complete_task, fail_task. One instance is built per resolved
/// session, bound to that session's project and agent, so "me" in every method
/// means the agent that's actually calling it - an agent can only claim,
/// complete, or fail tasks assigned to itself.
///
/// Task creation is asynchronous by design: create_task only records the task.
/// Nothing runs until someone starts `maxwell-cli work` (or the assigned agent
/// is next opened and picks it up), and the tool descriptions say so, so a model
/// doesn't sit waiting for a result that isn't coming in this conversation.
/// </summary>
public sealed class TaskBoardTool(TaskStore store, MaxwellConfigRepository config, string projectId, string agentName)
{
    [Description(
        "Create a task on this project's shared task board and assign it to another agent (or yourself). " +
        "This only records the task - it does NOT run it and you will not get a result in this conversation. " +
        "The assigned agent picks it up later (the user runs `maxwell-cli work`). Make the description " +
        "self-contained: the assignee starts from a fresh session and sees only what you write here, " +
        "so include file paths, requirements, and what 'done' looks like.")]
    public string CreateTask(
        [Description("Name of the agent that should do the work - must be an agent defined in agents.json.")]
        string assignedAgent,
        [Description("A complete, self-contained description of the work.")]
        string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "Error: description must not be empty.";
        }

        var agents = config.LoadAgents();
        var target = agents.FirstOrDefault(a => string.Equals(a.Name, assignedAgent?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            var known = agents.Count == 0 ? "(none)" : string.Join(", ", agents.Select(a => a.Name));
            return $"Error: no agent named '{assignedAgent}'. Known agents: {known}.";
        }

        var item = store.Create(projectId, target.Name, agentName, description.Trim());
        return $"Created {item.Id}, assigned to {item.AssignedAgent} (pending). It has not been run - " +
               "it will start when the user runs `maxwell-cli work`.";
    }

    [Description(
        "List tasks on this project's task board. Use scope 'assigned_to_me' (default) to see your own " +
        "queue, 'created_by_me' to check on tasks you delegated (including their results), or 'all'.")]
    public string ListTasks(
        [Description("\"assigned_to_me\" (default), \"created_by_me\", or \"all\".")]
        string scope = "assigned_to_me",
        [Description("Optional filter: \"pending\", \"inprogress\", \"done\", or \"failed\".")]
        string? status = null)
    {
        WorkItemStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<WorkItemStatus>(status, ignoreCase: true, out var parsed))
            {
                return $"Error: unrecognized status '{status}'. Use one of: {string.Join(", ", Enum.GetNames<WorkItemStatus>())}.";
            }

            statusFilter = parsed;
        }

        var normalizedScope = (scope ?? "assigned_to_me").Trim().ToLowerInvariant();
        if (normalizedScope is not ("assigned_to_me" or "created_by_me" or "all"))
        {
            return $"Error: unrecognized scope '{scope}'. Use assigned_to_me, created_by_me, or all.";
        }

        IEnumerable<WorkItem> items = store.Load(projectId);
        items = normalizedScope switch
        {
            "assigned_to_me" => items.Where(t => IsMe(t.AssignedAgent)),
            "created_by_me" => items.Where(t => IsMe(t.CreatedBy)),
            _ => items,
        };

        var matches = items
            .Where(t => statusFilter is null || t.Status == statusFilter)
            .OrderBy(t => t.CreatedAt)
            .ToList();

        if (matches.Count == 0)
        {
            return "No tasks match.";
        }

        var builder = new StringBuilder();
        foreach (var task in matches)
        {
            builder.AppendLine($"- {task.Id} [{task.Status}] assigned to {task.AssignedAgent} by {task.CreatedBy}: {task.Description}");
            if (!string.IsNullOrWhiteSpace(task.Result))
            {
                builder.AppendLine($"    result: {Shorten(task.Result, 500)}");
            }
        }

        return builder.ToString();
    }

    [Description("Mark a task assigned to you as in progress. Optional - complete_task works on a pending task too.")]
    public string ClaimTask(
        [Description("The task id, e.g. task-1a2b3c.")]
        string taskId)
    {
        var task = store.Find(projectId, taskId);
        if (task is null) return $"Error: no task with id '{taskId}'.";
        if (!IsMe(task.AssignedAgent)) return $"Error: {task.Id} is assigned to {task.AssignedAgent}, not you.";

        switch (task.Status)
        {
            case WorkItemStatus.InProgress:
                return $"{task.Id} is already in progress - go ahead with it.";
            case WorkItemStatus.Pending:
                store.Update(projectId, task.Id, t =>
                {
                    t.Status = WorkItemStatus.InProgress;
                    t.StartedAt = DateTimeOffset.UtcNow;
                });
                return $"Claimed {task.Id}. Do the work, then call complete_task (or fail_task).";
            default:
                return $"Error: {task.Id} is already {task.Status}.";
        }
    }

    [Description("Mark a task assigned to you as done, recording a short result for whoever created it.")]
    public string CompleteTask(
        [Description("The task id.")]
        string taskId,
        [Description("What you did and where any output lives (file paths, key findings). Keep it concise.")]
        string result)
        => Finish(taskId, WorkItemStatus.Done, result);

    [Description("Mark a task assigned to you as failed because you cannot complete it, with the reason.")]
    public string FailTask(
        [Description("The task id.")]
        string taskId,
        [Description("Why the task could not be completed, and anything that was done before giving up.")]
        string reason)
        => Finish(taskId, WorkItemStatus.Failed, reason);

    private string Finish(string taskId, WorkItemStatus outcome, string text)
    {
        var task = store.Find(projectId, taskId);
        if (task is null) return $"Error: no task with id '{taskId}'.";
        if (!IsMe(task.AssignedAgent)) return $"Error: {task.Id} is assigned to {task.AssignedAgent}, not you.";
        if (task.Status is WorkItemStatus.Done or WorkItemStatus.Failed) return $"Error: {task.Id} is already {task.Status}.";

        store.Update(projectId, task.Id, t =>
        {
            t.Status = outcome;
            t.CompletedAt = DateTimeOffset.UtcNow;
            t.Result = text;
        });

        return $"Marked {task.Id} as {outcome}.";
    }

    private bool IsMe(string name) => string.Equals(name, agentName, StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
