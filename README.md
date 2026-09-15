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

## On-disk layout

```
{HomeDirectory}/.maxwell/
  connections.json          # AI provider connections
  agents.json                # named agents -> {connection, model}
  skills/{AgentName}/        # optional: AgentSkills folder for this agent
  instructions/{AgentName}.md
  plugins/{pluginFolder}/    # optional: plugin.json + entry assembly (see below)
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
default `llama-cpp` connection (`http://localhost:8080/v1`) and two default
agents — `Maxwell` (general-purpose) and `SkillSmith` (see below) — edit
`connections.json`/`agents.json` to point at your actual llama.cpp server and
model name.

## Skills

An agent's `skills/{AgentName}/` folder (Home and/or Working, see layout
above) holds subfolders in the [Agent Skills format](https://agentskills.io) -
the same open standard used by Claude Code, GitHub Copilot, and others.
Maxwell doesn't invent its own skill format; `SkillLoader` hands each folder
straight to `AgentSkillsDotNet`'s loader, so anything that's a valid Agent
Skill elsewhere is a valid Agent Skill here.

```
skills/{AgentName}/
  {skill-name}/
    SKILL.md          # required — YAML frontmatter + Markdown instructions
    scripts/...       # optional — code the agent can read and run itself
    references/...    # optional — extra docs loaded on demand
    assets/...         # optional — templates, sample configs, ...
```

`SKILL.md`:

```markdown
---
name: pdf-processing
description: Extract PDF text, fill forms, merge files. Use when handling PDFs.
---
# PDF processing

Detailed instructions for the agent to follow when this skill is active...
```

`name` (max 64 chars, lowercase/numbers/hyphens, must match the folder name
exactly) and `description` (max 1024 chars, non-empty) are required;
`license`, `compatibility`, `metadata`, and `allowed-tools` are optional. See
the [full specification](https://agentskills.io/specification) for exact
constraints.

### SkillSmith

`SkillSmith` is a second default agent whose only job is writing new skills
for your other agents. It only has `read`/`edit`/`write`/`validate_skill` —
no `bash` — scoped to `{HomeDirectory}/.maxwell/skills/` rather than your
working directory, and `validate_skill` re-parses whatever it just wrote
through the real Agent Skills loader so it gets an honest pass/fail instead of
guessing at YAML correctness:

```bash
maxwell-cli --agent SkillSmith
> I want Maxwell to be able to summarize git log output. Make it a skill.
```

It will ask what it needs to (which agent, what triggers the skill), write
`{HomeDirectory}/.maxwell/skills/Maxwell/{skill-name}/SKILL.md`, validate it,
fix anything the validator flags, and tell you when it's ready — `Maxwell`
picks it up automatically next time it resolves its skills, no restart
needed. If you already had an `agents.json` before this update, add the
`SkillSmith` entry by hand (the bootstrapper only seeds `agents.json` when the
file doesn't exist yet):

```json
{ "name": "SkillSmith", "connection": "llama-cpp", "model": "gemma", "clientType": "ChatCompletion", "toolProfile": "SkillAuthoring" }
```

## Plugins

Drop a folder under `{HomeDirectory}/.maxwell/plugins/` containing a
`plugin.json` and a compiled entry assembly:

```json
{
  "id": "maxwell-anthropic",
  "version": "0.1.0",
  "entryAssembly": "Maxwell.Plugin.Anthropic.dll"
}
```

The assembly should reference `Maxwell.Agents` and contain a public type
implementing `Maxwell.Agents.Plugins.IMaxwellPlugin`, with a public
parameterless constructor:

```csharp
public sealed class AnthropicPlugin : IMaxwellPlugin
{
    public string Id => "maxwell-anthropic";

    public void ConfigureProviders(IPluginProviderRegistry providers) =>
        providers.Register("Anthropic", new AnthropicAgentProvider());

    public void ConfigureTools(IPluginToolRegistry tools) { } // no extra tools
}
```

Each plugin loads into its own collectible `AssemblyLoadContext`, so its
private dependencies can't clash with Maxwell's or with another plugin's.
Registered providers become usable by setting `clientType` in
`connections.json` to whatever string the provider was registered under (e.g.
`"Anthropic"`); registered tools are available to every agent, in addition to
the built-in read/bash/edit/write tools and each agent's own skills. A plugin
that fails to load (bad manifest, missing assembly, exception in its
`Configure*` methods) is skipped rather than stopping Maxwell from starting.

## Hooks

A plugin can also observe or intervene at specific points of a chat turn by
implementing `Maxwell.Agents.Hooks.IChatHook` and registering it in
`ConfigureHooks`:

```csharp
public sealed class AuditLogPlugin : IMaxwellPlugin
{
    public string Id => "maxwell-audit-log";

    public void ConfigureHooks(IPluginHookRegistry hooks) => hooks.Register(new AuditLogHook());
}

public sealed class AuditLogHook : IChatHook
{
    public Task OnBeforeToolCallAsync(ToolCallContext context, CancellationToken cancellationToken)
    {
        if (context.ToolName == "bash" && context.Arguments.TryGetValue("command", out var cmd)
            && cmd?.ToString()?.Contains("rm -rf") == true)
        {
            context.Block("bash commands containing 'rm -rf' are not allowed by policy.");
        }

        return Task.CompletedTask;
    }

    public Task OnTurnCompletedAsync(TurnCompletedContext context, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine($"[{context.Session.SessionId}] {context.Usage?.TotalTokenCount} tokens");
        return Task.CompletedTask;
    }
}
```

Every method on `IChatHook` has a no-op default, so a hook only implements the
stage(s) it needs. The stages, in the order they fire for one user turn:

| Stage | When | Can it change the outcome? |
|---|---|---|
| `OnUserPromptAsync` | Before the message is sent to the model | Yes — rewrite `context.Prompt` |
| `OnBeforeToolCallAsync` | Immediately before a tool executes | Yes — mutate `context.Arguments`, or call `context.Block(reason)` to prevent execution |
| `OnAfterToolCallAsync` | Immediately after a tool executes (or throws) | Yes — set `context.Result` to redact/transform what the model sees, or recover from an exception |
| `OnResponseChunkAsync` | For each piece of streamed reply/reasoning text | No — observation only |
| `OnTurnCompletedAsync` | Once the full turn is assembled, before it's persisted | No — observation only |

`OnBeforeToolCallAsync`/`OnAfterToolCallAsync` are the only stages that can
actually stop a tool from running: they wrap the tool's real invocation
directly, whereas by the time a tool call is visible anywhere else it has
already executed. Multiple hooks run in registration order; for blocking, the
first hook to call `Block` wins.

