using Maxwell.Agents.Models;
using Microsoft.Extensions.AI;

namespace Maxwell.Agents.Hooks;

/// <summary>
/// The one hook pipeline for the process: the host registers nothing by default
/// (Maxwell ships with no built-in hooks), and <see cref="Plugins.PluginLoader"/>
/// adds whatever each plugin registers via <see cref="IChatHook.OnUserPromptAsync"/>
/// et al. <see cref="MaxwellSessionService"/> drives the stream-level stages
/// directly from <see cref="MaxwellSessionService.StreamAsync"/>, and
/// <see cref="HookedAIFunction"/> drives the tool-level stages from inside each
/// wrapped tool's invocation.
/// </summary>
public sealed class HookPipeline : IPluginHookRegistry
{
    private readonly List<IChatHook> _hooks = [];

    public void Register(IChatHook hook) => _hooks.Add(hook);

    /// <summary>Lets callers skip tool-wrapping/context-allocation entirely when no plugin registered a hook.</summary>
    internal bool HasHooks => _hooks.Count > 0;

    public async Task<string> RunUserPromptAsync(HookSessionInfo session, string prompt, CancellationToken cancellationToken)
    {
        if (_hooks.Count == 0)
        {
            return prompt;
        }

        var context = new UserPromptContext { Session = session, Prompt = prompt };
        foreach (var hook in _hooks)
        {
            await hook.OnUserPromptAsync(context, cancellationToken);
        }

        return context.Prompt;
    }

    internal async Task RunBeforeToolCallAsync(ToolCallContext context, CancellationToken cancellationToken)
    {
        foreach (var hook in _hooks)
        {
            await hook.OnBeforeToolCallAsync(context, cancellationToken);
            if (context.IsBlocked)
            {
                break; // first hook to block wins; later hooks don't get a vote on an already-blocked call
            }
        }
    }

    internal async Task RunAfterToolCallAsync(ToolResultContext context, CancellationToken cancellationToken)
    {
        foreach (var hook in _hooks)
        {
            await hook.OnAfterToolCallAsync(context, cancellationToken);
        }
    }

    public async Task RunResponseChunkAsync(HookSessionInfo session, string text, bool isReasoning, CancellationToken cancellationToken)
    {
        if (_hooks.Count == 0)
        {
            return;
        }

        var context = new ResponseChunkContext { Session = session, Text = text, IsReasoning = isReasoning };
        foreach (var hook in _hooks)
        {
            await hook.OnResponseChunkAsync(context, cancellationToken);
        }
    }

    public async Task RunTurnCompletedAsync(
        HookSessionInfo session,
        string userMessage,
        string assistantReply,
        string? reasoning,
        UsageDetails? usage,
        TokenSpeed? speed,
        CancellationToken cancellationToken)
    {
        if (_hooks.Count == 0)
        {
            return;
        }

        var context = new TurnCompletedContext
        {
            Session = session,
            UserMessage = userMessage,
            AssistantReply = assistantReply,
            Reasoning = reasoning,
            Usage = usage,
            Speed = speed,
        };
        foreach (var hook in _hooks)
        {
            await hook.OnTurnCompletedAsync(context, cancellationToken);
        }
    }
}
