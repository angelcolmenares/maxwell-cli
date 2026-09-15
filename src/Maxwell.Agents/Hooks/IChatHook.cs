namespace Maxwell.Agents.Hooks;

/// <summary>
/// Observes or intervenes in a chat turn at one or more points in its lifecycle.
/// Every method has a no-op default, so a plugin only overrides the stage(s) it
/// actually cares about - a redaction hook might implement only
/// <see cref="OnAfterToolCallAsync"/>, an audit-log hook only
/// <see cref="OnTurnCompletedAsync"/>.
///
/// Call order for one user turn:
/// OnUserPromptAsync -> (OnBeforeToolCallAsync -> [tool runs] -> OnAfterToolCallAsync)* -> OnResponseChunkAsync* -> OnTurnCompletedAsync
/// The tool-call pair can repeat zero or more times per turn, interleaved with
/// response chunks, matching however many tool calls the model makes.
///
/// Multiple hooks run in registration order (built-in host hooks, if any, before
/// plugin hooks; plugins in the order <see cref="Plugins.PluginLoader"/> loaded
/// them). For <see cref="OnBeforeToolCallAsync"/>, the first hook to call
/// <see cref="ToolCallContext.Block"/> wins and later hooks are skipped for that
/// call.
/// </summary>
public interface IChatHook
{
    Task OnUserPromptAsync(UserPromptContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    Task OnBeforeToolCallAsync(ToolCallContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    Task OnAfterToolCallAsync(ToolResultContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    Task OnResponseChunkAsync(ResponseChunkContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    Task OnTurnCompletedAsync(TurnCompletedContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Narrow write-only view of <see cref="HookPipeline"/> handed to plugins.</summary>
public interface IPluginHookRegistry
{
    void Register(IChatHook hook);
}
