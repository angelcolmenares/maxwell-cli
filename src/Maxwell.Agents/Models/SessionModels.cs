using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Maxwell.Agents.Models;

/// <summary>
/// One entry in a project's sessions.json index file.
/// </summary>
public sealed class SessionSummary
{
    public string SessionId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Agent { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Set when this session was created by continuing an existing session with a
    /// different agent (see docs/design-notes.md, "switching agents mid-session").
    /// </summary>
    public string? BranchedFromSessionId { get; set; }
}

/// <summary>
/// Full content of a {sessionId}.json chat file: the agent-framework thread state
/// (so the conversation can be resumed with full model context) plus a
/// human-readable transcript (so the chat can be displayed/exported without
/// re-hydrating an AIAgent).
/// </summary>
public sealed class SessionRecord
{
    public string SessionId { get; set; } = string.Empty;
    public string Agent { get; set; } = string.Empty;
    public string Connection { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Set when this session was created by continuing an existing session with a
    /// different agent (see docs/design-notes.md, "switching agents mid-session").
    /// </summary>
    public string? BranchedFromSessionId { get; set; }

    /// <summary>
    /// Result of AIAgent.SerializeSessionAsync(session). Fed back into
    /// AIAgent.DeserializeSessionAsync(...) to resume the conversation with full
    /// model-visible context.
    /// </summary>
    public JsonElement? SerializedThread { get; set; }

    /// <summary>Human-readable transcript, oldest first.</summary>
    public List<ChatTurnRecord> Turns { get; set; } = new();

    /// <summary>
    /// Running total of token usage across every assistant turn in this session
    /// (built up via UsageDetails.Add(...) as each reply comes in).
    /// </summary>
    public UsageDetails? CumulativeUsage { get; set; }
}

public sealed class ChatTurnRecord
{
    public required string Role { get; set; } // "user" | "assistant"
    public required string Content { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// Token usage for this specific turn (assistant turns only - populated from
    /// AgentResponse.Usage once the streamed reply completes).
    /// </summary>
    public UsageDetails? Usage { get; set; }

    /// <summary>
    /// Reasoning/"thinking" text the model produced en route to this reply
    /// (assistant turns only, from streamed TextReasoningContent items). Kept
    /// separate from Content, which is the final answer only.
    /// </summary>
    public string? Reasoning { get; set; }

    /// <summary>Generation speed for this turn (assistant turns only).</summary>
    public TokenSpeed? Speed { get; set; }
}

/// <summary>
/// Wall-clock generation speed for one streamed reply, derived from
/// UsageDetails.OutputTokenCount and a Stopwatch spanning the whole
/// RunStreamingAsync call (see MaxwellSessionService.StreamAsync).
/// </summary>
public sealed class TokenSpeed
{
    public long OutputTokenCount { get; set; }
    public double TotalDurationSeconds { get; set; }
    public double TokensPerSecond { get; set; }

    /// <summary>Time from sending the request to the first token (of any kind, reasoning or answer) arriving.</summary>
    public double? TimeToFirstTokenSeconds { get; set; }
}
