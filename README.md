# Maxwell

A reusable .NET 10 / C# library (`Maxwell.Agents`) plus a console app (`maxwell-cli`,
project `Maxwell.Cli`) for running local AI agents against a llama.cpp server, with
resumable, on-disk chat sessions.

```
maxwell/
  Maxwell.sln
  src/
    Maxwell.Agents/   class library: config, storage, providers, sessions, tools, plugins, hooks, built-in skills
    Maxwell.Cli/       console app: arg parsing, chat loop, `work` and `tasks` commands
  tests/
    Maxwell.Agents.Tests/   xunit tests
    skill-scripts/          Node tests for the web-search skill's scripts
```

## Contents

- [Build and run](#build-and-run)
- [On-disk layout](#on-disk-layout)
- [Skills](#skills): [SkillSmith](#skillsmith), [AgentSmith](#agentsmith), [Built-in skills](#built-in-skills)
- **[Browsing and searching the web](#browsing-and-searching-the-web)**: [Quick start](#quick-start), [`web-browser`](#the-web-browser-skill), [`web-search`](#the-web-search-skill), [Setting up SearXNG](#setting-up-searxng)
- [Tasks and `maxwell-cli work`](#tasks-and-maxwell-cli-work)
- [Tool discovery](#tool-discovery) (how agents find tools; not web search)
- [Plugins](#plugins)
- [Hooks](#hooks)

## Build and run

```bash
dotnet build
dotnet run --project src/Maxwell.Cli -- --agent Maxwell
```

Run the tests: `dotnet test` covers the tool search (stemmer, ranking, and a
table of "what an agent might ask for -> which tool should come back"), the
`search_tools`/`call_tool` round trip, and the built-in skills; and
`node --test tests/skill-scripts/*.test.mjs` (Node.js 18+) tests the scripts that
ship with the `web-search` skill against small local servers.

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
  seeded-skills.txt         # built-in skills already copied here (see Built-in skills)
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

### Built-in skills

Maxwell ships skills of its own (embedded from `src/Maxwell.Agents/BuiltInSkills/`) and
copies them into `skills/{AgentName}/` on first run, so an agent has them without anyone
writing them first. Currently: `web-browser` and `web-search`, both for `Maxwell` (see
[Browsing and searching the web](#browsing-and-searching-the-web)).

It's a one-time gift, not a sync. Each skill is recorded in `seeded-skills.txt`, so:

- **a skill you delete stays deleted**;
- **a skill you edit is never overwritten**;
- **an existing folder with the same name wins**.

A skill added in a later version is seeded next to the ones already handled, so updating
Maxwell gives existing installs the new skills. To get a fresh copy of one, delete its
folder *and* its line in `seeded-skills.txt`. To give another agent a built-in skill, copy
the folder into that agent's `skills/{AgentName}/`.

## Browsing and searching the web

Two built-in skills give the `Maxwell` agent access to the web:

| Skill | Use it to | Needs |
|---|---|---|
| [`web-browser`](#the-web-browser-skill) | open a page, read it, click, fill in forms, take screenshots or PDFs | Node.js 18+, `@playwright/cli`, a browser |
| [`web-search`](#the-web-search-skill) | look something up online and answer with sources | Node.js 18+; optionally SearXNG (Docker); falls back to `web-browser` |

Both are ordinary skills (a `SKILL.md`, plus two small scripts for search) that use the
`bash` tool the agent already has. There is no browser code in Maxwell itself.

### Quick start

1. **Check Node.js** (18 or newer): `node --version`
2. **Install the browser tool**, once:
   ```bash
   npm install -g @playwright/cli@latest
   playwright-cli install-browser        # downloads Chromium
   playwright-cli --version              # sanity check
   ```
3. *(Optional, recommended for search)* **set up SearXNG**: see
   [Setting up SearXNG](#setting-up-searxng). Without it, search still works through the browser.
4. **Check the skills are installed.** Maxwell copies them into your skills folder when it
   starts (see [Built-in skills](#built-in-skills)):
   ```bash
   ls ~/.maxwell/skills/Maxwell          # PowerShell: dir $HOME\.maxwell\skills\Maxwell
   # expected: web-browser  web-search
   ```
5. **Start Maxwell and ask in plain language**:
   ```
   maxwell-cli
   > Open https://example.com and tell me the page title and the main heading.
   > Search for the latest .NET 10 release notes and summarize them. Give me the sources.
   ```

There is no command to "run" a skill: you ask, and the agent decides to use it. If it
doesn't, name it: *"Use the web-search skill to ..."* (small local models sometimes need
that nudge). You can write in Spanish or any other language; the agent writes its search
queries in English, because the skills and tool descriptions are in English.

### The `web-browser` skill

Teaches the agent to drive a real browser with
[`playwright-cli`](https://github.com/microsoft/playwright-cli), Microsoft's Playwright
command line built for coding agents.

**What it can do:** open and navigate pages; read them; click, type, select and upload
files; work with several tabs; take screenshots and save PDFs; run a little JavaScript to
pull out a value.

**Things to ask:**

```
> Open https://playwright.dev, find the Python install command, and tell me what it is.
> Go to http://localhost:5000 and log in with user `test` / password `test123` (a dev account), then tell me what you see.
> Take a full-page screenshot of https://example.com and save it as example.png.
> Save https://example.com/docs as a PDF called docs.pdf.
> Open the pricing page of <site> with a visible window so I can watch.
```

**What you'll see.** Each step is a `bash` call running `playwright-cli ...`, shown in the
chat like any other tool call. The browser is headless (invisible) unless you ask to watch
(`--headed`). The agent works from text **snapshots** of the page (a list of elements with
refs like `e21`) rather than screenshots, so it works with models that can't see images.
To keep context small it is taught to avoid reading whole pages: `find <text>` returns only
matching elements, and `snapshot --depth=3`, `snapshot <ref>` and `snapshot --filename=...`
take part of a page or save it to a file. Snapshots and screenshots are saved in your
working directory, usually under `.playwright-cli/`; they may contain personal data, so
don't commit that folder.

**Managing browsers.** The agent should `close` the browser when it finishes. If one is
left running, `playwright-cli list` shows open browsers, and `playwright-cli close-all` or
`kill-all` clears them (headless sessions also shut down by themselves after an hour idle).

**If something goes wrong:**

| Symptom | Likely cause | What to do |
|---|---|---|
| The agent says `playwright-cli` isn't installed, or `command not found` | Not installed, or the terminal predates the install | `npm install -g @playwright/cli@latest`, then open a **new** terminal. The skill also tries `npx @playwright/cli` |
| The browser won't start | Chromium hasn't been downloaded | `playwright-cli install-browser` |
| It clicks the wrong thing or can't find an element | Element refs go stale when a page changes | Ask it to take a fresh snapshot and retry |
| The page looks empty | Still loading, a cookie or consent banner, or a login wall | Ask it to snapshot again and describe what it sees |
| It doesn't use the skill | Skill missing, or the model didn't pick it | Check step 4 of the quick start; say *"Use the web-browser skill to ..."* |
| Browsers keep running after the chat | The agent forgot to close | `playwright-cli close-all` (or `kill-all`) |

**Safety.** The skill tells the agent to treat page content as untrusted data (a page can
try to give it instructions); to start every session fresh, with no saved logins; not to
enter credentials you didn't give it for the task; and to ask before anything hard to undo
(purchases, posting, deleting). In an unattended `maxwell-cli work` run it can't ask, so it
fails the task instead. That is guidance to the model, **not enforcement**: `bash` isn't
sandboxed, so for real enforcement use a [hook](#hooks) (for example, one that blocks `bash`
commands you don't want).

### The `web-search` skill

Lets the agent look things up online and answer with sources. It has two layers, tried in
order:

1. **SearXNG** (preferred). [SearXNG](https://docs.searxng.org/) is a self-hosted search
   aggregator with a JSON API, so the agent gets clean, compact results (title, URL,
   snippet) from one command, with no browser. Used when the `SEARXNG_URL` environment
   variable is set.
2. **DuckDuckGo in the browser** (fallback). When SearXNG isn't configured or doesn't
   answer, the agent opens a DuckDuckGo results page with `playwright-cli` and extracts the
   result links.

Then it reads the best two or three results and answers with the URLs it used. Reading a page
uses a small script that prints its readable text (no scripts, styles or navigation, cut to
a size limit) instead of dumping HTML into context; pages that need JavaScript go through
`web-browser`.

**Things to ask:**

```
> Search for the latest .NET 10 release notes and summarize them. Give me the sources.
> Busca qué cambió en la última versión de Playwright y dame las fuentes.
> Look up how to enable JSON output in SearXNG and show me the exact setting.
> What is the current stable version of Node.js? Check the official site.
```

**What you'll see.** The agent runs a script and gets numbered results like this, then opens
the best ones and answers:

```
1. Result title
   https://example.org/page
   A short snippet from the page...
2. ...

Showing 8 of 23 results.
```

#### Setting up SearXNG

Optional, but recommended: it is faster, cheaper in context, and doesn't need a browser. Needs
Docker.

```bash
mkdir -p ~/searxng
docker run -d --name searxng -p 8888:8080 -v ~/searxng:/etc/searxng searxng/searxng
# SearXNG only enables HTML output by default. Edit ~/searxng/settings.yml and under `search:` add
#   formats:
#     - html
#     - json
docker restart searxng
curl -s "http://localhost:8888/search?q=test&format=json" | head -c 300    # should print JSON
```

The exact location of the settings file can vary with the image version; see the
[SearXNG docs](https://docs.searxng.org/admin/installation.html). Then tell Maxwell where it
is, **in the shell that launches `maxwell-cli`**:

```bash
export SEARXNG_URL=http://localhost:8888          # Git Bash / Linux / macOS
$env:SEARXNG_URL = "http://localhost:8888"        # PowerShell
```

(On Windows, `setx SEARXNG_URL http://localhost:8888` makes it permanent for new terminals.)
Maxwell's `bash` tool runs through Git Bash on Windows. The skill's scripts are **Node.js**,
not Python, so the only runtime needed is the Node.js 18+ that `playwright-cli` already
requires.

**Check it works before asking Maxwell:**

```bash
node ~/.maxwell/skills/Maxwell/web-search/scripts/searxng-search.mjs "test"          # Git Bash / Linux / macOS
node $HOME\.maxwell\skills\Maxwell\web-search\scripts\searxng-search.mjs "test"    # PowerShell
```

It should print numbered results. If not, the message says why; see the table below.

**If something goes wrong:**

| What you see | Meaning | What to do |
|---|---|---|
| `SEARXNG_URL is not set` | Maxwell can't see the variable | Set it in the shell that launches `maxwell-cli`, then restart it. Meanwhile the agent falls back to the browser |
| `SEARXNG_URL must start with http://` | The address has no scheme | Use `http://localhost:8888`, not `localhost:8888` |
| `Could not reach SearXNG at ...` | Container stopped, or wrong port | `docker ps`, then `docker start searxng`; check the `8888:8080` mapping |
| `403 Forbidden`, or `did not return JSON` | JSON output isn't enabled, or the bot limiter refuses API calls | Add `json` to `search.formats` in `settings.yml`; check `server.limiter`; restart the container |
| `No results ...` plus `Engines that did not respond: google (CAPTCHA)` | The engines SearXNG scrapes are blocking it | Try again later, or enable other engines in SearXNG. The agent rephrases up to three times, then falls back |
| The DuckDuckGo fallback returns nothing, or a CAPTCHA | DuckDuckGo is blocking the IP | The agent stops and tells you. Open the same search once in your own browser, or set up SearXNG |
| `web-search script not found` | The skill folder was moved, deleted, or is incomplete | Delete the `~/.maxwell/skills/Maxwell/web-search` folder **and** its line in `~/.maxwell/seeded-skills.txt`, then restart Maxwell to restore it |
| `Very little text found` when reading a page | The page builds its content with JavaScript | The agent should use `web-browser` for it; ask for that if it doesn't |

**What to expect.** Search engines are hostile to automation. A self-hosted SearXNG still
scrapes engines such as Google and DuckDuckGo, which can answer with CAPTCHAs or block an
IP, so results can be thinner on some days; the script says which engines didn't respond.
The browser fallback can hit the same CAPTCHA, and the skill's rule is to **stop and tell
you** rather than try to get around it (in an interactive chat it may offer to reopen the
page in a visible window so you can solve it yourself). Queries go to the engines SearXNG
aggregates, so the agent is told not to put secrets in them. As with the browser skill, the
safety rules are guidance to the model, not enforcement. Some search engines' terms of
service restrict automated queries: fine for light personal use, but worth knowing.

### Using the web skills elsewhere

- **In `maxwell-cli work` (unattended).** They work, but nobody can answer questions, so
  anything needing confirmation (logins, purchases, posting) fails the task instead.
  Example: have an agent `create_task` for another agent: *"Search for X and write a summary
  to `notes/x.md`"*, then run `maxwell-cli work`.
- **For another agent.** Copy the skill folder (for example
  `~/.maxwell/skills/Maxwell/web-search`) into that agent's folder,
  `~/.maxwell/skills/<AgentName>/`. (`AgentSmith` can create the agent and write its
  instructions, but it has no `bash`, so it can't copy skill folders: do that step by hand.)
- **Only for one project.** Put the folder in the project's `.maxwell/skills/<AgentName>/`
  instead (both locations are checked).

## Tasks and `maxwell-cli work`

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

### How tool search works

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

