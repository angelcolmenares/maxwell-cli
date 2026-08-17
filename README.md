# Maxwell

A reusable .NET 10 / C# library (`Maxwell.Agents`) plus a console app (`maxwell-cli`,
project `Maxwell.Cli`) for running local AI agents against a llama.cpp server, with
resumable, on-disk chat sessions.

```
maxwell/
  Maxwell.sln
  src/
    Maxwell.Agents/   class library: config, storage, provider, session orchestration
    Maxwell.Cli/       console app: arg parsing + chat loop
```

## Build & run

```bash
dotnet build
dotnet run --project src/Maxwell.Cli -- --agent Maxwell
```

To use it as the `maxwell-cli` command from any directory, per the spec:

```bash
dotnet pack src/Maxwell.Cli -c Release
dotnet tool install --global --add-source src/Maxwell.Cli/nupkg maxwell-cli
maxwell-cli
maxwell-cli --session 121313
maxwell-cli --session 121313 --agent MyAgent
maxwell-cli --agent MyAgent
```

> **This solution has not been compiled or run in this environment** — the sandbox
> used to write it has no .NET SDK installed and no access to nuget.org, so
> `dotnet restore` could not be exercised here. The code was written against the
> real, current public APIs of `Microsoft.Agents.AI` 1.17.0 and
> `AgentFrameworkToolkit.OpenAI` 1.17.0 (verified via their NuGet/GitHub docs), but
> please run `dotnet build` locally as a first step and expect to fix any small API
> drift (see "Assumptions" below for the parts most likely to need adjustment).

## On-disk layout

```
{HomeDirectory}/.maxwell/
  connections.json          # AI provider connections
  agents.json                # named agents -> {connection, model}
  skills/{AgentName}/        # optional: AgentSkills folder for this agent
  instructions/{AgentName}.md
  projects/{ProjectId}/
    sessions.json            # index: id, title, agent, timestamps
    {sessionId}.json         # full transcript + serialized AgentSession

{WorkingDirectory}/.maxwell/
  project.json                          # { "id": "<ProjectId>" }
  skills/{AgentName}/                   # optional additions to the Home skills
  instructions/{AgentName}.md           # optional override of the Home instructions
```

`ProjectId` is derived from the full, sanitized path of the working directory
(so two folders named e.g. `app` in different locations get separate session
histories). `{HomeDirectory}/.maxwell` is bootstrapped on first run with a
default `llama-cpp` connection (`http://localhost:8080/v1`) and a default
`Maxwell` agent — edit `connections.json`/`agents.json` to point at your actual
llama.cpp server and model name.

## Design notes / assumptions

The instructions left a few things implicit; here's what was assumed, and why:

- **`connections.js` / `agents.json`** — the spec's tree shows `connections.js` but
  the content is JSON and everything else is `.json`; treated as a typo and
  implemented as `connections.json`.
- **`TheFullNameOfTheFolder`** — read as "the working directory's full path",
  sanitized into a filesystem-safe id, rather than just the leaf folder name, to
  avoid collisions between same-named folders in different places.
- **Switching `--agent` on an existing `--session`** — the spec says this should
  either "create a new session based on it" *or* "just continue" with the new
  agent, which are two different behaviors. This implementation continues the
  session if the given agent matches the session's original agent, and otherwise
  **branches**: a new session id is created (so history isn't lost or corrupted),
  its `branchedFromSessionId` points back at the source session, but the new
  session starts with a **fresh** conversation thread rather than replaying the
  old transcript into the new agent. This is because `AgentSession` instances are
  documented as unsafe to hand from one agent/provider to a different one. If you
  want the new agent to have context from the source session, the simplest fix is
  to prepend a summary of `{sourceSessionId}.json`'s `turns` to the new agent's
  instructions before the first message — there's a natural extension point for
  that in `MaxwellSessionService.ResolveAsync`.
- **Skills** — `skills/{AgentName}/` folders are wired up via
  `AgentFrameworkToolkit`'s `AgentSkillsFactory` (turns a skills folder into extra
  tools + extra instructions) through the separate `AgentSkillsDotNet` package.
  This is the part of the code with the least amount of directly-confirmed API
  surface, so it's implemented as best-effort in `SkillLoader.cs`: any failure to
  load skills is swallowed and the agent simply runs without them. Delete the
  `AgentSkillsDotNet` package reference and `SkillLoader.cs` if you'd rather not
  depend on it.
- **AI providers** — per the spec, only one provider is implemented: an
  OpenAI-compatible endpoint, which is how llama.cpp's server is reached
  (`connections.json`'s `clientType: "OpenAI"` + `options.endpoint` pointed at your
  llama.cpp instance). `LlamaCppAgentProvider` is intentionally the only place that
  branches on `clientType`, so adding Anthropic/Azure/etc. later is a matter of
  adding another `AgentFrameworkToolkit.*` package reference and another branch.
- **Session ids** are generated as `session-{timestamp}-{shortguid}`; passing your
  own `--session <id>` (e.g. `--session 121313`) works too — if that id doesn't
  exist yet, it's created fresh under that literal id.
- **Streaming** — the CLI uses `AIAgent.RunStreamingAsync(...)`, printing each
  `AgentResponseUpdate.Text` chunk as it arrives (`MaxwellSessionService.StreamAsync`,
  consumed via `await foreach` in `ChatConsole`). The full reply is still
  accumulated and written to the session file once the stream completes, exactly
  as the non-streaming version did.
- **Token usage** — `StreamAsync` collects every streamed `AgentResponseUpdate`,
  reassembles them with `updates.ToAgentResponse()` once the stream ends, and reads
  `response.Usage` (a `Microsoft.Extensions.AI.UsageDetails`: input/output/total
  token counts, plus provider-specific `AdditionalCounts`). That's stored two
  ways in `{sessionId}.json`: per-turn, on each assistant `ChatTurnRecord.Usage`,
  and cumulatively, on `SessionRecord.CumulativeUsage` (built up via
  `UsageDetails.Add(...)`). After each reply, the CLI prints both the turn's
  counts and the running session total, plus the latest turn's input-token count
  as an approximation of "how full the context window is" (accurate as long as
  the model isn't silently trimming/summarizing older turns itself).
- **Reasoning / "thinking" display** — `LlamaCppAgentProvider` now requests
  `ClientType.ResponsesApi`, which is what makes reasoning content available on
  streamed updates. `StreamAsync` inspects each `AgentResponseUpdate.Contents`
  item: `TextReasoningContent` is treated as "thinking" text, `TextContent` as
  the actual answer, and both are yielded separately as `StreamChunk(Text,
  IsReasoning)` so the CLI can render them differently (dim gray "(thinking)" vs.
  the normal cyan reply). Reasoning text is also persisted per-turn, on
  `ChatTurnRecord.Reasoning`, kept separate from the final `Content`. Whether a
  given model/backend actually emits reasoning content depends on the model —
  if it doesn't, `IsReasoning` chunks simply never occur and the CLI behaves as
  before.
- **Generation speed** — `StreamAsync` runs a `Stopwatch` for the whole
  `RunStreamingAsync` call and records the elapsed time to the first streamed
  token (reasoning or answer). Once the stream ends, `UsageDetails.OutputTokenCount`
  divided by total elapsed time gives tokens/sec; both are stored per-turn as
  `ChatTurnRecord.Speed` (`TokenSpeed`: output token count, total duration,
  tokens/sec, time-to-first-token) and printed by the CLI right under the token
  usage line, e.g. `[speed] 42 tokens in 3.10s → 13.5 tok/s (first token: 0.21s)`.
  Like token usage, this depends on the backend actually reporting
  `OutputTokenCount` in `Usage` — if it doesn't, the speed line is simply
  omitted.
- **Per-agent `clientType` (OpenAI wire protocol)** — `AgentConfig` now has an
  optional `clientType` field (`"ChatCompletion"` or `"ResponsesApi"`), read by
  `LlamaCppAgentProvider.ParseClientType` and defaulting to `ChatCompletion` when
  omitted or unrecognized. This exists because llama.cpp's server only speaks
  Chat Completions: pointing it at the Responses API fails with `llama.cpp does
  not support 'previous_response_id'`. Reasoning/"thinking" content (see above)
  requires `ResponsesApi`, so set `"clientType": "ResponsesApi"` on an agent only
  when its connection actually supports that (e.g. real OpenAI, or a
  Responses-API-compatible proxy) — not on agents pointed at llama.cpp. Example:
  `{"name":"MyAgent","connection":"openai","model":"gpt-5","clientType":"ResponsesApi"}`.
