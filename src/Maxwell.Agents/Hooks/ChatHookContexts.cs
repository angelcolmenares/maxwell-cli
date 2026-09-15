using Maxwell.Agents.Models;
using Microsoft.Extensions.AI;

namespace Maxwell.Agents.Hooks;

/// <summary>Identifies which agent/project/session a hook invocation belongs to.</summary>
public sealed class HookSessionInfo
{
    public required string AgentName { get; set; }
    public required string ProjectId { get; set; }
    public required string SessionId { get; set; }
}

/// <summary>Fired once per <see cref="MaxwellSessionService.StreamAsync"/> call, before the message is sent to the model.</summary>
public sealed class UserPromptContext
{
    public required HookSessionInfo Session { get; init; }

    /// <summary>The user's message. Set this to rewrite what actually gets sent to the model and recorded in the transcript.</summary>
    public required string Prompt { get; set; }
}

/// <summary>
/// Fired immediately before a tool executes. This is the only stage that can
/// actually prevent execution - see <see cref="Block"/>.
/// </summary>
public sealed class ToolCallContext
{
    public required HookSessionInfo Session { get; init; }
    public required string ToolName { get; init; }

    /// <summary>Arguments the model supplied, parsed by parameter name. Mutate values in place to rewrite what the tool receives.</summary>
    public required AIFunctionArguments Arguments { get; init; }

    public bool IsBlocked { get; private set; }
    public string? BlockReason { get; private set; }

    /// <summary>
    /// Prevents the underlying tool from running. <paramref name="reason"/> is
    /// returned to the model as the tool's result, so it can be told plainly
    /// why (e.g. "blocked: bash commands may not target /etc").
    /// </summary>
    public void Block(string reason)
    {
        IsBlocked = true;
        BlockReason = reason;
    }
}

/// <summary>Fired after a tool executes (or throws).</summary>
public sealed class ToolResultContext
{
    public required HookSessionInfo Session { get; init; }
    public required string ToolName { get; init; }
    public required AIFunctionArguments Arguments { get; init; }

    /// <summary>
    /// The tool's return value. Set this to redact or transform what the model
    /// sees. If <see cref="Exception"/> is set, setting this "recovers" from the
    /// exception instead of letting it propagate.
    /// </summary>
    public object? Result { get; set; }

    /// <summary>Non-null if the tool threw and no earlier hook has recovered by setting <see cref="Result"/>.</summary>
    public Exception? Exception { get; init; }
}

/// <summary>
/// Fired for each piece of streamed reply text (mirrors <see cref="StreamChunk"/>).
/// Observation-only in this version - hooks cannot rewrite what's shown live, only
/// react to it (e.g. scan for a policy violation and cancel the call via the
/// CancellationToken it's given). Called once per chunk, so keep handlers cheap.
/// </summary>
public sealed class ResponseChunkContext
{
    public required HookSessionInfo Session { get; init; }
    public required string Text { get; init; }
    public required bool IsReasoning { get; init; }
}

/// <summary>Fired once a turn is fully assembled, right before it's persisted. Observation-only - useful for audit logging, telemetry, or cost tracking.</summary>
public sealed class TurnCompletedContext
{
    public required HookSessionInfo Session { get; init; }
    public required string UserMessage { get; init; }
    public required string AssistantReply { get; init; }
    public string? Reasoning { get; init; }
    public UsageDetails? Usage { get; init; }
    public TokenSpeed? Speed { get; init; }
}
