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
default `llama-cpp` connection (`http://localhost:8080/v1`) and a default
`Maxwell` agent — edit `connections.json`/`agents.json` to point at your actual
llama.cpp server and model name.

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

