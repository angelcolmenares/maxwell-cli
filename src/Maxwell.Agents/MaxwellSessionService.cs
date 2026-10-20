using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Maxwell.Agents.Hooks;
using Maxwell.Agents.Models;
using Maxwell.Agents.Plugins;
using Maxwell.Agents.Providers;
using Maxwell.Agents.Storage;
using Maxwell.Agents.Tools;
using Maxwell.Agents.Tools.Search;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Maxwell.Agents;

public sealed class SessionResolutionRequest
{
    public string? SessionId { get; init; }
    public string? AgentName { get; init; }

    /// <summary>
    /// When true (default), the agent's open tasks are appended to its
    /// instructions so it knows about its queue from the first turn. `work`
    /// turns this off: it runs one specific task per fresh session, and showing
    /// the whole queue would invite the agent to start on the others.
    /// </summary>
    public bool IncludeTaskQueue { get; init; } = true;

    /// <summary>
    /// Names of discoverable tools to load directly for this session instead of leaving them
    /// behind search_tools/call_tool. For tools the session's prompt tells the agent to use
    /// (e.g. `work` pins complete_task/fail_task), so it doesn't have to search before it can
    /// finish its job.
    /// </summary>
    public IReadOnlyList<string>? PinnedTools { get; init; }
}

public sealed class ActiveSession
{
    public required AIAgent Agent { get; init; }

    /// <summary>
    /// AgentSession is the current base abstraction for a conversation's state
    /// (it replaced the older AgentThread type). Created via
    /// AIAgent.CreateSessionAsync() for a new conversation, or restored via
    /// AIAgent.DeserializeSessionAsync(...) to resume one.
    /// </summary>
    public required AgentSession Session { get; init; }

    public required SessionRecord Record { get; init; }
    public required string ProjectId { get; init; }

    /// <summary>True when this call created a brand-new session id.</summary>
    public required bool IsNewSession { get; init; }
}

/// <summary>
/// Ties together config loading, agent construction and session persistence so
/// the CLI only has to deal with "resolve a session, then stream messages".
/// </summary>
public sealed class MaxwellSessionService
{
    private readonly MaxwellPaths _paths;
    private readonly MaxwellBootstrapper _bootstrapper;
    private readonly MaxwellConfigRepository _config;
    private readonly SessionStore _sessions;
    private readonly TaskStore _tasks;
    private readonly SkillLoader _skillLoader = new();

    private readonly AgentProviderRegistry _providers = new();

    /// <summary>The one hook pipeline for the process; populated by plugins' <see cref="IMaxwellPlugin.ConfigureHooks"/>.</summary>
    private readonly HookPipeline _hooks = new();

    /// <summary>Tools contributed by plugins' <see cref="IMaxwellPlugin.ConfigureTools"/>, loaded once at startup.</summary>
    private readonly IReadOnlyList<AITool> _pluginTools;

    /// <summary>Search keywords per discoverable tool name: the task board's built-ins plus whatever plugins declared.</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _toolKeywords = new(StringComparer.OrdinalIgnoreCase);

    private readonly IToolSearchStrategy _toolSearch;

    /// <summary>Load results for every plugin folder found, for diagnostics (e.g. a future `plugins list` command).</summary>
    public IReadOnlyList<LoadedPlugin> LoadedPlugins { get; }

    /// <summary>The task board store - used by <see cref="Work.WorkRunner"/> and the CLI's `tasks`/`work` commands.</summary>
    public TaskStore Tasks => _tasks;

    /// <summary>The id of the project (working directory) this process is running in, creating its .maxwell folders if needed.</summary>
    public string GetProjectId() => _bootstrapper.EnsureWorkingStructure().Id;

    /// <param name="toolSearchStrategy">How search_tools matches queries to catalog tools. Defaults to <see cref="LexicalToolSearchStrategy"/>.</param>
    public MaxwellSessionService(MaxwellPaths paths, IToolSearchStrategy? toolSearchStrategy = null)
    {
        _paths = paths;
        _bootstrapper = new MaxwellBootstrapper(paths);
        _config = new MaxwellConfigRepository(paths);
        _sessions = new SessionStore(paths);
        _tasks = new TaskStore(paths);
        _toolSearch = toolSearchStrategy ?? new LexicalToolSearchStrategy();

        // Built-in providers register first, so a plugin can deliberately
        // override "OpenAI" (or add "Anthropic", "AzureOpenAI", ...) via
        // last-registration-wins in AgentProviderRegistry.
        _providers.Register("OpenAI", new OpenAIAgentProvider());

        _bootstrapper.EnsureHomeStructure(); // plugins live under the Home dir; make sure it exists before scanning it
        foreach (var (toolName, keywords) in AgentToolset.TaskBoardKeywords)
        {
            _toolKeywords[toolName] = keywords;
        }

        var pluginTools = new List<AITool>();
        LoadedPlugins = new PluginLoader(_paths).LoadAll(_providers, pluginTools, _toolKeywords, _hooks);
        _pluginTools = pluginTools;
    }

    public async Task<ActiveSession> ResolveAsync(SessionResolutionRequest request)
    {
        _bootstrapper.EnsureHomeStructure();
        var project = _bootstrapper.EnsureWorkingStructure();

        var existingSummary = request.SessionId is not null
            ? _sessions.FindSummary(project.Id, request.SessionId)
            : null;

        var existingRecord = existingSummary is not null
            ? _sessions.LoadRecord(project.Id, existingSummary.SessionId)
            : null;

        // Switching --agent on an existing session branches into a new session id
        // instead of reusing the old session: an AgentSession is created by, and
        // may carry behaviors specific to, the AIAgent that created it, so it
        // isn't safe to hand one agent's session to a different agent.
        var isBranch = existingRecord is not null
            && request.AgentName is not null
            && !string.Equals(request.AgentName, existingRecord.Agent, StringComparison.OrdinalIgnoreCase);

        var agentName = request.AgentName
                         ?? existingRecord?.Agent
                         ?? MaxwellBootstrapper.DefaultAgentName;

        var agentConfig = _config.GetAgentOrThrow(agentName);
        var connectionConfig = _config.GetConnectionOrThrow(agentConfig.Connection);

        var instructions = _config.LoadInstructions(agentName);
        var skillDirs = _config.GetSkillDirectories(agentName);
        var (skillTools, extraInstructions) = _skillLoader.Load(skillDirs);
        if (!string.IsNullOrWhiteSpace(extraInstructions))
        {
            instructions = $"{instructions}\n\n{extraInstructions}";
        }

        var queueSection = request.IncludeTaskQueue ? BuildTaskQueueSection(project.Id, agentConfig.Name) : null;
        if (queueSection is not null)
        {
            instructions = $"{instructions}\n\n{queueSection}";
        }

        // Every agent gets a tool profile for free (Filesystem by default; see
        // AgentConfig.ToolProfile), rooted appropriately for that profile. These,
        // plus the skill mechanism's own tools, are the agent's "core" tools:
        // always loaded.
        var profile = Enum.TryParse<AgentToolProfile>(agentConfig.ToolProfile, ignoreCase: true, out var parsedProfile)
            ? parsedProfile
            : AgentToolProfile.Filesystem;

        IReadOnlyList<AITool> builtInTools = profile switch
        {
            AgentToolProfile.SkillAuthoring => AgentToolset.CreateSkillAuthoringTools(_paths.HomeSkillsDir),
            AgentToolProfile.AgentAuthoring => AgentToolset.CreateAgentAuthoringTools(_paths, _config),
            _ => AgentToolset.CreateBuiltInTools(_paths.WorkingRoot),
        };

        // Everything else (the task board, plugin-contributed tools) is "discoverable":
        // not loaded up front, but reachable through search_tools/call_tool, so the
        // model's context only carries the tools it actually needs.
        var taskBoardTools = AgentToolset.CreateTaskBoardTools(_tasks, _config, project.Id, agentConfig.Name);
        IReadOnlyList<AITool> discoverableTools = [.. taskBoardTools, .. _pluginTools];

        List<AITool> coreTools = [.. builtInTools, .. skillTools];
        List<AIFunction> catalogTools = [];

        if (agentConfig.ToolDiscovery ?? true)
        {
            var pinned = new HashSet<string>(request.PinnedTools ?? [], StringComparer.OrdinalIgnoreCase);
            if (queueSection is not null)
            {
                // The queue section above tells the agent to use these, so don't make it search first.
                pinned.UnionWith(["claim_task", "complete_task", "fail_task"]);
            }

            foreach (var tool in discoverableTools)
            {
                // Only AIFunctions can be run through call_tool; any other kind of tool stays directly loaded.
                if (tool is AIFunction function && !pinned.Contains(function.Name))
                {
                    catalogTools.Add(function);
                }
                else
                {
                    coreTools.Add(tool);
                }
            }
        }
        else
        {
            coreTools.AddRange(discoverableTools);
        }

        // SessionId isn't known yet (it's only assigned below once we know
        // whether this is a new session or a resumed one), so HookSessionInfo is
        // built now with a placeholder and patched in place afterwards - the
        // wrapped tools close over this same instance, so by the time any tool
        // actually runs (during StreamAsync, always after this method returns)
        // it already has the real SessionId.
        var hookSession = new HookSessionInfo { AgentName = agentName, ProjectId = project.Id, SessionId = string.Empty };

        List<AITool> tools = WrapWithHooks(coreTools, hookSession);

        if (catalogTools.Count > 0)
        {
            // Catalog tools are wrapped individually, so hooks see "bash"/"create_task"/... by
            // their real names. The two meta-tools themselves are deliberately left unwrapped:
            // wrapping call_tool too would fire every hook twice for one call.
            var wrappedCatalog = WrapWithHooks(catalogTools, hookSession).Cast<AIFunction>().ToList();
            tools.AddRange(AgentToolset.CreateToolDiscoveryTools(wrappedCatalog, _toolSearch, _toolKeywords));
            instructions = $"{instructions}\n\n{BuildToolDiscoverySection(wrappedCatalog)}";
        }

        var provider = _providers.Resolve(connectionConfig.ClientType);
        var agent = provider.CreateAgent(connectionConfig, agentConfig, instructions, tools);

        AgentSession agentSession;
        SessionRecord record;
        bool isNewSession;

        if (existingRecord is not null && !isBranch)
        {
            agentSession = existingRecord.SerializedThread is { } serialized
                ? await agent.DeserializeSessionAsync(serialized)
                : await agent.CreateSessionAsync();
            record = existingRecord;
            isNewSession = false;
        }
        else
        {
            agentSession = await agent.CreateSessionAsync();
            record = new SessionRecord
            {
                SessionId = isBranch ? SessionStore.NewSessionId() : request.SessionId ?? SessionStore.NewSessionId(),
                Agent = agentName,
                Connection = connectionConfig.Name,
                Model = agentConfig.Model,
                CreatedAt = DateTimeOffset.UtcNow,
                BranchedFromSessionId = isBranch ? existingRecord!.SessionId : null,
            };
            isNewSession = true;
        }

        hookSession.SessionId = record.SessionId;

        return new ActiveSession
        {
            Agent = agent,
            Session = agentSession,
            Record = record,
            ProjectId = project.Id,
            IsNewSession = isNewSession,
        };
    }

    /// <summary>
    /// Sends one user turn and streams the assistant's reply chunk by chunk (via
    /// AIAgent.RunStreamingAsync), split into reasoning/"thinking" chunks and
    /// final-answer chunks (see <see cref="StreamChunk"/>). Persists the full
    /// turn (answer text + reasoning text + token usage + generation speed) and
    /// the updated session state once the stream completes.
    ///
    /// The assistant's <see cref="ChatTurnRecord"/> is appended to
    /// session.Record.Turns *before* this method returns, so callers can read
    /// session.Record.Turns[^1] right after draining the stream (see ChatConsole).
    /// </summary>
    private List<AITool> WrapWithHooks(IEnumerable<AITool> tools, HookSessionInfo hookSession) =>
        tools
            .Select(tool => _hooks.HasHooks && tool is AIFunction function
                ? new HookedAIFunction(function, _hooks, hookSession)
                : tool)
            .ToList();

    /// <summary>
    /// Appended to an agent's instructions when some of its tools are behind search_tools. Lists
    /// the tool *names* only (cheap), as a nudge: a small model won't search for a capability
    /// it has no idea exists.
    /// </summary>
    private static string BuildToolDiscoverySection(IReadOnlyList<AIFunction> catalog) =>
        $"""
        ## Additional tools (on demand)

        Besides your core tools, more tools are available on demand so they don't crowd your context:
        {ToolCatalogTool.NameHint(catalog)}

        When a task needs something your core tools can't do, call search_tools with a short description
        of what you need (e.g. "assign work to another agent"). It returns each matching tool's
        description and parameters. Then run the tool with call_tool(name, arguments). Don't guess a
        tool's name or parameters - search first, then call.
        """;

    /// <summary>
    /// The "you have mail" part of agent coordination: if tasks are waiting for
    /// this agent, say so in its instructions so it walks into its first turn
    /// already knowing. (Nothing pushes to a running agent - Maxwell is one process
    /// per invocation - so "checked at session start" is the signal.)
    /// </summary>
    private string? BuildTaskQueueSection(string projectId, string agentName)
    {
        var open = _tasks.Load(projectId)
            .Where(t => (t.Status is WorkItemStatus.Pending or WorkItemStatus.InProgress)
                        && string.Equals(t.AssignedAgent, agentName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.CreatedAt)
            .ToList();

        if (open.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("## Your task queue");
        builder.AppendLine();
        builder.AppendLine($"{open.Count} open task(s) are assigned to you on this project's task board:");
        foreach (var task in open)
        {
            builder.AppendLine($"- {task.Id} [{task.Status}] from {task.CreatedBy}: {task.Description}");
        }

        builder.AppendLine();
        builder.AppendLine(
            "Let the user know about them if it's relevant. To work on one: claim_task, do the work, then " +
            "complete_task (or fail_task if you can't). `maxwell-cli work` also runs pending tasks unattended.");
        return builder.ToString();
    }

    public async IAsyncEnumerable<StreamChunk> StreamAsync(
        ActiveSession session,
        string userMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var hookSession = new HookSessionInfo
        {
            AgentName = session.Record.Agent,
            ProjectId = session.ProjectId,
            SessionId = session.Record.SessionId,
        };

        userMessage = await _hooks.RunUserPromptAsync(hookSession, userMessage, cancellationToken);

        session.Record.Turns.Add(new ChatTurnRecord
        {
            Role = "user",
            Content = userMessage,
            Timestamp = DateTimeOffset.UtcNow,
        });

        var fullReply = new StringBuilder();
        var reasoning = new StringBuilder();

        // Collect the raw updates (not just their text) so we can reassemble a
        // full AgentResponse afterwards and read its Usage.
        List<AgentResponseUpdate> updates = [];

        var stopwatch = Stopwatch.StartNew();
        double? timeToFirstTokenSeconds = null;

        await foreach (var update in session.Agent.RunStreamingAsync(userMessage, session.Session, cancellationToken: cancellationToken))
        {
            updates.Add(update);

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent { Text.Length: > 0 } reasoningContent:
                        timeToFirstTokenSeconds ??= stopwatch.Elapsed.TotalSeconds;
                        reasoning.Append(reasoningContent.Text);
                        await _hooks.RunResponseChunkAsync(hookSession, reasoningContent.Text, isReasoning: true, cancellationToken);
                        yield return new StreamChunk(reasoningContent.Text, IsReasoning: true);
                        break;

                    case TextContent { Text.Length: > 0 } textContent:
                        timeToFirstTokenSeconds ??= stopwatch.Elapsed.TotalSeconds;
                        fullReply.Append(textContent.Text);
                        await _hooks.RunResponseChunkAsync(hookSession, textContent.Text, isReasoning: false, cancellationToken);
                        yield return new StreamChunk(textContent.Text, IsReasoning: false);
                        break;
                }
            }
        }

        stopwatch.Stop();

        AgentResponse response = updates.ToAgentResponse();
        UsageDetails? usage = response.Usage;

        TokenSpeed? speed = null;
        if (usage?.OutputTokenCount is { } outputTokens && outputTokens > 0 && stopwatch.Elapsed.TotalSeconds > 0)
        {
            speed = new TokenSpeed
            {
                OutputTokenCount = outputTokens,
                TotalDurationSeconds = stopwatch.Elapsed.TotalSeconds,
                TokensPerSecond = outputTokens / stopwatch.Elapsed.TotalSeconds,
                TimeToFirstTokenSeconds = timeToFirstTokenSeconds,
            };
        }

        var assistantReply = fullReply.ToString();
        var assistantReasoning = reasoning.Length > 0 ? reasoning.ToString() : null;

        session.Record.Turns.Add(new ChatTurnRecord
        {
            Role = "assistant",
            Content = assistantReply,
            Timestamp = DateTimeOffset.UtcNow,
            Usage = usage,
            Reasoning = assistantReasoning,
            Speed = speed,
        });

        if (usage is not null)
        {
            session.Record.CumulativeUsage ??= new UsageDetails();
            session.Record.CumulativeUsage.Add(usage);
        }

        session.Record.SerializedThread = await session.Agent.SerializeSessionAsync(session.Session);

        await _hooks.RunTurnCompletedAsync(hookSession, userMessage, assistantReply, assistantReasoning, usage, speed, cancellationToken);

        _sessions.SaveRecord(session.ProjectId, session.Record);
    }
}
