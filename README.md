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

Run the tests (`dotnet test`) - they cover the tool search (stemmer, ranking, and a
table of "what an agent might ask for -> which tool should come back") and the
`search_tools`/`call_tool` round trip.

To use it as the `maxwell-cli` command from any directory, per the spec:

```bash
dotnet pack src/Maxwell.Cli -c Release
dotnet tool install --global --add-source src/Maxwell.Cli/nupkg maxwell-cli
maxwell-cli
maxwell-cli --session 121313
maxwell-cli --session 121313 --agent MyAgent
maxwell-cli --agent MyAgent
maxwell-cli work            # run this project's pending tasks (see Tasks)
maxwell-cli tasks           # list this project's task board
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
    tasks.json               # the project's shared task board (see Tasks)
    {sessionId}.json         # full transcript + serialized AgentSession

{WorkingDirectory}/.maxwell/
  project.json                          # { "id": "<ProjectId>" }
  skills/{AgentName}/                   # optional additions to the Home skills
  instructions/{AgentName}.md           # optional override of the Home instructions
```

`ProjectId` is derived from the full, sanitized path of the working directory
(so two folders named e.g. `app` in different locations get separate session
histories). `{HomeDirectory}/.maxwell` is bootstrapped on first run with a
default `llama-cpp` connection (`http://localhost:8080/v1`) and three default
agents — `Maxwell` (general-purpose), `SkillSmith`, and `AgentSmith` (see
below) — edit `connections.json`/`agents.json` to point at your actual
llama.cpp server and model name.

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

### AgentSmith

`AgentSmith` builds new specialist agents end to end from a plain-language
request - registering the entry in `agents.json`, writing its instructions,
and (if the task needs it) authoring a skill for it, all in one conversation.
It doesn't delegate to `SkillSmith` for the skill-authoring part; it has the
same skill-authoring tools itself (renamed `read_skill_file`/`write_skill_file`/
`edit_skill_file` to avoid colliding with its instructions tools), plus
`read_instructions`/`write_instructions` and `list_connections`/`list_agents`/
`register_agent`. Like `SkillSmith`, no `bash` — this is declarative authoring
(files + registry entries), not general-purpose computer use.

```bash
maxwell-cli --agent AgentSmith
> Necesito un agente especialista en Excel, que pueda crear y actualizar archivos de Excel.
```

It will ask whatever it actually needs (a name, which connection/model to use
if more than one exists) rather than guessing, register the new agent with a
sensible default tool profile (`Filesystem` — read/bash/edit/write on your
working directory, so it can shell out to whatever it needs to manipulate
files), write its instructions, and write + validate a skill for it if the
task calls for one. The new agent is usable immediately:
`maxwell-cli --agent <name>`. `AgentSmith` itself defaults to `AgentAuthoring`
for its tool profile - add it by hand the same way as `SkillSmith` if you
already had an `agents.json`:

```json
{ "name": "AgentSmith", "connection": "llama-cpp", "model": "gemma", "clientType": "ChatCompletion", "toolProfile": "AgentAuthoring" }
```

## Tasks & `maxwell-cli work`

Agents can hand work to each other through a per-project task board
(`tasks.json`). Every agent can reach five tools for it, whatever its tool
profile: `create_task`, `list_tasks`, `claim_task`, `complete_task`,
`fail_task`. They're not loaded up front - agents find them through
[tool discovery](#tool-discovery) (`search_tools("assign work to another
agent")`, then `call_tool`). The exceptions are loaded directly because the
prompt tells the agent to use them: `complete_task`/`fail_task` during
`maxwell-cli work`, and `claim_task`/`complete_task`/`fail_task` when an agent
is opened with tasks waiting in its queue.

```
you> (to an orchestrator agent) Split the Q3 report into data cleanup and charts.
orchestrator> search_tools("assign work to another agent")
orchestrator> call_tool("create_task", {assignedAgent: "ExcelExpert", description: "Clean sales_q3.xlsx: ..."}) -> task-1a2b3c
              call_tool("create_task", {assignedAgent: "ChartMaker", description: "Build charts from the cleaned data: ..."}) -> task-4d5e6f

$ maxwell-cli work
▶ task-1a2b3c → ExcelExpert: Clean sales_q3.xlsx: ...
...
✔ task-1a2b3c done
▶ task-4d5e6f → ChartMaker: Build charts ...
✔ task-4d5e6f done

Finished: 2 done, 0 failed.
```

**It's asynchronous on purpose.** `create_task` only records the task - Maxwell
is one process per invocation, so nothing is listening to be "signaled". The
assigned agent picks the work up either way:

- `maxwell-cli work` runs every pending task unattended, oldest first, each in
  a **fresh session** for its assigned agent (so a task description must be
  self-contained - the tool descriptions tell agents this). The agent is told
  no human is available, and must end by calling `complete_task` or
  `fail_task`. If it forgets, it gets one nudge; if it still doesn't, the task
  is marked `Failed` (with its last reply as context) rather than assumed done.
  Tasks run one at a time, and tasks that workers create during the run are
  picked up in the same run. `--agent <name>` limits it to one agent's tasks;
  `--max-tasks <n>` (default 20) caps the run so two agents can't create tasks
  for each other forever. Exits `1` if any task failed. Ctrl+C stops cleanly
  and puts the running task back to pending.
- Or just open the assigned agent interactively: its open tasks are listed in
  its instructions at session start, so it can tell you about them and work
  through them with you.

The creator checks on its delegated work later with
`list_tasks(scope: "created_by_me")`, which includes each task's result.
`maxwell-cli tasks` shows the whole board, including the session id that
worked each task (resume it with `maxwell-cli --session <id>` to see exactly
what the agent did).

A task left `InProgress` by a crashed run isn't retried automatically - edit
its `status` back to `pending` in `tasks.json` to re-run it.

## Tool discovery

Every tool an agent has loaded costs context on every request, and small local
models get worse at picking the right one as the list grows. So an agent
starts with only its **core** tools - for an ordinary agent, `read`, `bash`,
`edit`, `write` (plus whatever its skills provide) - and reaches everything
else through two meta-tools:

- `search_tools(query)` - describe what you need in a few words; returns the
  matching tools' descriptions, parameters, and a ready-to-copy example call.
  If nothing matches it lists the available tool names instead.
- `call_tool(name, arguments)` - runs a tool found that way, with an object of
  parameter values. Errors come back with the tool's expected parameters so the
  model can retry.

The agent's instructions get a short "Additional tools (on demand)" section
naming what's available (names only - schemas stay out of context until
searched), since a model won't search for a capability it doesn't know exists.
The search result stays in the conversation, so later turns call the tool
without searching again.

What's in the catalog: the task board tools and any tool a plugin registers
through `IPluginToolRegistry`. A tool's profile tools (e.g.
`SkillAuthoring`'s `validate_skill`) are core, not catalog: they're the point of
that agent. Hooks still see the real tool name and arguments for catalog tools
(`bash`, `create_task`, ...), not just `call_tool`.

### How search works

Matching is pluggable (`IToolSearchStrategy`); the default,
`LexicalToolSearchStrategy`, is keyword search with no model or network, so it's
instant and testable:

1. The query and each tool's text are split into words, lowercased, stripped of
   stopwords, and reduced to **Porter stems** - "delegating", "delegates" and
   "delegated" all match each other.
2. Each query word is weighted by how **distinctive** it is across the catalog
   (IDF): "task" is in every tool of a task board and says little; a word found in
   one tool says a lot.
3. A tool scores per matching word: **name** (x4), a single-word **keyword** (x3),
   **description** (x1).
4. **Decisive evidence** adds more: a query that is exactly a tool's name (returns
   just that tool); a multi-word keyword whose words are all in the query ("hand
   off"); or a **literal phrase** the query contains as written (below).
5. How many results come back depends on how sure the evidence is. With decisive
   evidence, only hits scoring at least **40%** of the best are kept, so one clear
   match isn't padded ("assign work to another agent" returns `create_task`
   alone). With only loose word overlap the query is **ambiguous**, so the bar
   drops to **25%** and plausible runners-up come back too: the agent reads each
   description and picks, and a wrong top-1 costs little when the right tool is also
   in the list.

**Hints.** A tool can declare two kinds of extra search signals
(`ToolSearchHints`), which is how the task board tools find each other's synonyms:

- `Keywords` - words that mean the tool ("delegate", "dispatch"). Stemmed, so
  inflections match.
- `Phrases` - **literal** phrases, matched as written: no stemming, no stopword
  removal, word order kept (case and punctuation ignored). They exist because
  stemming erases exactly what separates some requests: "tasks I created" (list
  them) and "create a task" (make one) are the same words to a stemmer. With
  `list_tasks` declaring the phrase "tasks i created" and `create_task` declaring
  "create a task", each query finds its tool.

A plugin declares hints when it registers a tool:

```csharp
public void ConfigureTools(IPluginToolRegistry tools) =>
    tools.Register(myTool, new ToolSearchHints
    {
        Keywords = ["hand off", "dispatch", "enqueue"],
        Phrases = ["jobs i created"],
    });
// or, for keywords only: tools.Register(myTool, ["hand off", "dispatch"]);
```

A tool's **name, description, and hints** are what get searched, so write them for
that: say what the tool is *for*, in the words someone would use to ask.

**Language.** Tool names, descriptions, and hints are English, and so are the
stemmer and stopwords, so agents are told (in the instructions section and in
`search_tools`' own description) to **write the query in English even when the
user writes in another language**. A query in another language mostly finds
nothing; the "no match" reply lists the available tool names and repeats the
English instruction. A multilingual embedding strategy would remove this
constraint.

**Limits.** It matches words, not meaning, and has no typo tolerance: a synonym
nobody listed (as a keyword or in a description) won't match. That's what hints are
for, and what an embedding-based strategy would address: implement
`IToolSearchStrategy` and pass it to `MaxwellSessionService`'s constructor. If a
strategy throws (say, its embedding server is down), search falls back to the
lexical one instead of leaving the agent with no way to find tools. The tests
(`LexicalToolSearchStrategyTests`) are a table of "what an agent might ask -> which
tool should come back" over the real task board tools; the same table is the fair
way to compare any other strategy.

### Why a proxy

Why a `call_tool` proxy instead of adding found tools to the live tool list:
the list is fixed when the agent is created, and changing it mid-run depends
on framework internals; the proxy works with any provider and model. The cost
is that the model passes arguments as a nested object instead of using native
function calling for that tool. If that's less reliable than loading everything
for a capable hosted model, turn discovery off per agent:

```json
{ "name": "Maxwell", "connection": "openai", "model": "gpt-5", "toolDiscovery": false }
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
`"Anthropic"`); registered tools are available to every agent through
[tool discovery](#tool-discovery) (`search_tools`/`call_tool`), alongside the
built-in read/bash/edit/write tools and each agent's own skills. A plugin
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

