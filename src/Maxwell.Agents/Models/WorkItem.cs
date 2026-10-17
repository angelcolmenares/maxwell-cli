using System.Text.Json.Serialization;

namespace Maxwell.Agents.Models;

/// <summary>
/// Named WorkItem/WorkItemStatus (not Task/TaskStatus) on purpose: ImplicitUsings
/// pulls in System.Threading.Tasks, which already defines TaskStatus.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkItemStatus>))]
public enum WorkItemStatus
{
    /// <summary>Waiting for its assigned agent to pick it up (via `maxwell-cli work` or the agent itself).</summary>
    Pending,

    /// <summary>Claimed; an agent is (or was, if a run crashed) working on it.</summary>
    InProgress,

    Done,

    /// <summary>The agent called fail_task, the run errored, or the agent ended without completing it.</summary>
    Failed,
}

/// <summary>
/// One entry on a project's task board ({HomeDirectory}/.maxwell/projects/{ProjectId}/tasks.json).
/// Agents create these for each other with create_task; `maxwell-cli work` runs
/// the pending ones, one fresh session per task.
/// </summary>
public sealed class WorkItem
{
    public string Id { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AssignedAgent { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public WorkItemStatus Status { get; set; } = WorkItemStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>What the agent reported via complete_task/fail_task, or why a run failed.</summary>
    public string? Result { get; set; }

    /// <summary>The session that worked this task, so it can be inspected/resumed with `maxwell-cli --session`.</summary>
    public string? SessionId { get; set; }
}
