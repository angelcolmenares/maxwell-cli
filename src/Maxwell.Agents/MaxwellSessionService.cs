using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Maxwell.Agents.Models;
using Maxwell.Agents.Providers;
using Maxwell.Agents.Storage;
using Maxwell.Agents.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Maxwell.Agents;

public sealed class SessionResolutionRequest
{
    public string? SessionId { get; init; }
    public string? AgentName { get; init; }
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
    private readonly LlamaCppAgentProvider _provider = new();
    private readonly SkillLoader _skillLoader = new();

    public MaxwellSessionService(MaxwellPaths paths)
    {
        _paths = paths;
        _bootstrapper = new MaxwellBootstrapper(paths);
        _config = new MaxwellConfigRepository(paths);
        _sessions = new SessionStore(paths);
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

        // Every agent gets read/bash/edit/write for free, rooted at the working
        // directory Maxwell was launched from; skill-provided tools are added on top.
        List<AITool> tools = [.. AgentToolset.CreateBuiltInTools(_paths.WorkingRoot), .. skillTools];

        var agent = _provider.CreateAgent(connectionConfig, agentConfig, instructions, tools);

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
    public async IAsyncEnumerable<StreamChunk> StreamAsync(
        ActiveSession session,
        string userMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
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
                        yield return new StreamChunk(reasoningContent.Text, IsReasoning: true);
                        break;

                    case TextContent { Text.Length: > 0 } textContent:
                        timeToFirstTokenSeconds ??= stopwatch.Elapsed.TotalSeconds;
                        fullReply.Append(textContent.Text);
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

        session.Record.Turns.Add(new ChatTurnRecord
        {
            Role = "assistant",
            Content = fullReply.ToString(),
            Timestamp = DateTimeOffset.UtcNow,
            Usage = usage,
            Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null,
            Speed = speed,
        });

        if (usage is not null)
        {
            session.Record.CumulativeUsage ??= new UsageDetails();
            session.Record.CumulativeUsage.Add(usage);
        }

        session.Record.SerializedThread = await session.Agent.SerializeSessionAsync(session.Session);

        _sessions.SaveRecord(session.ProjectId, session.Record);
    }
}
