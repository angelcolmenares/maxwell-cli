using System.Text.Json;
using Maxwell.Agents.Models;

namespace Maxwell.Agents.Storage;

/// <summary>
/// Reads/writes a project's tasks.json. Every mutation is a read-modify-write
/// under one process-wide lock, so two tool calls in the same process can't
/// interleave and lose an update. (Separate maxwell-cli processes aren't
/// coordinated - `work` runs tasks one at a time, so in practice only one
/// process is writing at once.)
/// </summary>
public sealed class TaskStore(MaxwellPaths paths)
{
    private static readonly Lock Gate = new();

    public IReadOnlyList<WorkItem> Load(string projectId)
    {
        lock (Gate)
        {
            return LoadCore(projectId);
        }
    }

    public WorkItem? Find(string projectId, string taskId) =>
        Load(projectId).FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));

    public WorkItem Create(string projectId, string assignedAgent, string createdBy, string description)
    {
        lock (Gate)
        {
            var items = LoadCore(projectId);
            var item = new WorkItem
            {
                Id = NewTaskId(),
                Description = description,
                AssignedAgent = assignedAgent,
                CreatedBy = createdBy,
                Status = WorkItemStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            items.Add(item);
            SaveCore(projectId, items);
            return item;
        }
    }

    /// <summary>Applies <paramref name="mutate"/> to the task and saves it. Returns the updated task, or null if no such task exists.</summary>
    public WorkItem? Update(string projectId, string taskId, Action<WorkItem> mutate)
    {
        lock (Gate)
        {
            var items = LoadCore(projectId);
            var item = items.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                return null;
            }

            mutate(item);
            SaveCore(projectId, items);
            return item;
        }
    }

    private List<WorkItem> LoadCore(string projectId)
    {
        var file = paths.GetTasksFile(projectId);
        if (!File.Exists(file))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<WorkItem>>(File.ReadAllText(file), MaxwellJson.Options) ?? [];
    }

    private void SaveCore(string projectId, List<WorkItem> items)
    {
        Directory.CreateDirectory(paths.GetProjectDir(projectId));
        File.WriteAllText(paths.GetTasksFile(projectId), JsonSerializer.Serialize(items, MaxwellJson.Options));
    }

    // Short on purpose: small local models copy ids back into tool calls, and a
    // long GUID is an easy thing for them to mangle.
    private static string NewTaskId() => $"task-{Guid.NewGuid().ToString("N")[..6]}";
}
