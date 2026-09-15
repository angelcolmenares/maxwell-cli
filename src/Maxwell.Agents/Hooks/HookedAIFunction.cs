using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Maxwell.Agents.Hooks;

/// <summary>
/// Decorates a tool's underlying <see cref="AIFunction"/> so every invocation
/// runs through <see cref="HookPipeline"/>'s before/after tool-call hooks. This
/// is the only point in the pipeline where a hook can actually stop a tool from
/// running - by the time a tool call shows up as <see cref="FunctionCallContent"/>
/// in <c>AIAgent.RunStreamingAsync</c>'s update stream, Microsoft.Extensions.AI's
/// function-invocation middleware has already executed it internally.
///
/// <see cref="MaxwellSessionService"/> only wraps tools when
/// <see cref="HookPipeline.HasHooks"/> is true, so this type adds zero overhead
/// when no plugin registers a hook.
///
/// NOTE: <see cref="AIFunction"/>'s exact virtual member surface has shifted
/// slightly across Microsoft.Extensions.AI.Abstractions versions. This wrapper
/// only overrides members that have been stable since the 9.x line (Name,
/// Description, JsonSchema, InvokeCoreAsync) - build against your resolved
/// package version and let the compiler flag anything it renamed.
/// </summary>
internal sealed class HookedAIFunction(AIFunction inner, HookPipeline hooks, HookSessionInfo session) : AIFunction
{
    public override string Name => inner.Name;

    public override string Description => inner.Description;

    public override JsonElement JsonSchema => inner.JsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var callContext = new ToolCallContext
        {
            Session = session,
            ToolName = inner.Name,
            Arguments = arguments,
        };

        await hooks.RunBeforeToolCallAsync(callContext, cancellationToken);

        if (callContext.IsBlocked)
        {
            var blockedResult = $"Tool call blocked: {callContext.BlockReason}";

            var blockedContext = new ToolResultContext
            {
                Session = session,
                ToolName = inner.Name,
                Arguments = arguments,
                Result = blockedResult,
            };
            await hooks.RunAfterToolCallAsync(blockedContext, cancellationToken);

            return blockedContext.Result;
        }

        object? result = null;
        Exception? thrown = null;
        try
        {
            // Arguments may have been mutated in place by OnBeforeToolCallAsync -
            // this call sees whatever the hooks left behind.
            result = await inner.InvokeAsync(arguments, cancellationToken);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        var resultContext = new ToolResultContext
        {
            Session = session,
            ToolName = inner.Name,
            Arguments = arguments,
            Result = result,
            Exception = thrown,
        };
        await hooks.RunAfterToolCallAsync(resultContext, cancellationToken);

        // A hook "recovers" from a thrown exception by setting Result; otherwise
        // the original exception propagates rather than being silently swallowed.
        if (thrown is not null && resultContext.Result is null)
        {
            throw thrown;
        }

        return resultContext.Result;
    }
}
