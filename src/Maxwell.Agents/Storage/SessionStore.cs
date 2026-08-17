using System.Text.Json;
using Maxwell.Agents.Models;

namespace Maxwell.Agents.Storage;

/// <summary>
/// Reads/writes a project's sessions.json index and the individual
/// {sessionId}.json chat files.
/// </summary>
public sealed class SessionStore(MaxwellPaths paths)
{
    public IReadOnlyList<SessionSummary> LoadIndex(string projectId)
    {
        var file = paths.GetSessionsIndexFile(projectId);
        if (!File.Exists(file))
        {
            return [];
        }

        var json = File.ReadAllText(file);
        return JsonSerializer.Deserialize<List<SessionSummary>>(json, MaxwellJson.Options) ?? [];
    }

    public SessionSummary? FindSummary(string projectId, string sessionId) =>
        LoadIndex(projectId).FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));

    public void UpsertSummary(string projectId, SessionSummary summary)
    {
        var index = LoadIndex(projectId).ToList();
        var existingIndex = index.FindIndex(s => string.Equals(s.SessionId, summary.SessionId, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            index[existingIndex] = summary;
        }
        else
        {
            index.Add(summary);
        }

        var file = paths.GetSessionsIndexFile(projectId);
        File.WriteAllText(file, JsonSerializer.Serialize(index, MaxwellJson.Options));
    }

    public SessionRecord? LoadRecord(string projectId, string sessionId)
    {
        var file = paths.GetSessionFile(projectId, sessionId);
        if (!File.Exists(file))
        {
            return null;
        }

        var json = File.ReadAllText(file);
        return JsonSerializer.Deserialize<SessionRecord>(json, MaxwellJson.Options);
    }

    public void SaveRecord(string projectId, SessionRecord record)
    {
        record.UpdatedAt = DateTimeOffset.UtcNow;
        var file = paths.GetSessionFile(projectId, record.SessionId);
        File.WriteAllText(file, JsonSerializer.Serialize(record, MaxwellJson.Options));

        UpsertSummary(projectId, new SessionSummary
        {
            SessionId = record.SessionId,
            Title = DeriveTitle(record),
            Agent = record.Agent,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
        });
    }

    private static string DeriveTitle(SessionRecord record)
    {
        var firstUserTurn = record.Turns.FirstOrDefault(t => t.Role == "user");
        if (firstUserTurn is null)
        {
            return "New session";
        }

        var text = firstUserTurn.Content.Trim();
        return text.Length <= 60 ? text : text[..60] + "…";
    }

    public static string NewSessionId() =>
        $"session-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
}
