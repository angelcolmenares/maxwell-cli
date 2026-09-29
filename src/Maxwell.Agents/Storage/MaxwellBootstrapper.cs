using System.Text.Json;
using Maxwell.Agents.Models;

namespace Maxwell.Agents.Storage;

/// <summary>
/// Creates every directory/file Maxwell needs the first time it runs in a given
/// home or working directory, seeding sensible defaults (a llama.cpp connection
/// and a "Maxwell" agent) so a brand-new machine works out of the box.
/// </summary>
public sealed class MaxwellBootstrapper(MaxwellPaths paths)
{
    public const string DefaultAgentName = "Maxwell";
    public const string SkillSmithAgentName = "SkillSmith";
    public const string AgentSmithAgentName = "AgentSmith";
    private const string DefaultConnectionName = "llama-cpp";
    private const string DefaultEndpoint = "http://localhost:8080/v1";
    private const string DefaultModel = "gemma";

    /// <summary>Ensures {HomeDirectory}/.maxwell exists with default config files.</summary>
    public void EnsureHomeStructure()
    {
        Directory.CreateDirectory(paths.HomeMaxwellDir);
        Directory.CreateDirectory(paths.HomeSkillsDir);
        Directory.CreateDirectory(paths.HomeInstructionsDir);
        Directory.CreateDirectory(paths.ProjectsDir);
        Directory.CreateDirectory(paths.PluginsDir);
        Directory.CreateDirectory(paths.GetHomeAgentSkillsDir(DefaultAgentName));

        if (!File.Exists(paths.ConnectionsFile))
        {
            var defaults = new List<ConnectionConfig>
            {
                new()
                {
                    Name = DefaultConnectionName,
                    ClientType = "OpenAI",
                    Options = new ConnectionOptions { ApiKey = "no-key", Endpoint = DefaultEndpoint },
                },
            };
            File.WriteAllText(paths.ConnectionsFile, JsonSerializer.Serialize(defaults, MaxwellJson.Options));
        }

        if (!File.Exists(paths.AgentsFile))
        {
            var defaults = new List<AgentConfig>
            {
                new()
                {
                    Name = DefaultAgentName,
                    Connection = DefaultConnectionName,
                    Model = DefaultModel,
                    ClientType = "ChatCompletion",
                },
                new()
                {
                    Name = SkillSmithAgentName,
                    Connection = DefaultConnectionName,
                    Model = DefaultModel,
                    ClientType = "ChatCompletion",
                    ToolProfile = "SkillAuthoring",
                },
                new()
                {
                    Name = AgentSmithAgentName,
                    Connection = DefaultConnectionName,
                    Model = DefaultModel,
                    ClientType = "ChatCompletion",
                    ToolProfile = "AgentAuthoring",
                },
            };
            File.WriteAllText(paths.AgentsFile, JsonSerializer.Serialize(defaults, MaxwellJson.Options));
        }

        var defaultInstructionsFile = paths.GetHomeAgentInstructionsFile(DefaultAgentName);
        if (!File.Exists(defaultInstructionsFile))
        {
            File.WriteAllText(
                defaultInstructionsFile,
                "You are Maxwell, a helpful, direct AI assistant running locally via llama.cpp.\n");
        }

        var skillSmithInstructionsFile = paths.GetHomeAgentInstructionsFile(SkillSmithAgentName);
        if (!File.Exists(skillSmithInstructionsFile))
        {
            File.WriteAllText(skillSmithInstructionsFile, SkillSmithInstructions);
        }

        var agentSmithInstructionsFile = paths.GetHomeAgentInstructionsFile(AgentSmithAgentName);
        if (!File.Exists(agentSmithInstructionsFile))
        {
            File.WriteAllText(agentSmithInstructionsFile, AgentSmithInstructions);
        }
    }

    /// <summary>
    /// Seeded once into {HomeDirectory}/.maxwell/instructions/SkillSmith.md on
    /// first run. Encodes the Agent Skills spec (https://agentskills.io) inline
    /// so the model has it in context without needing to fetch anything, plus
    /// the write -> validate -> fix loop SkillSmith is expected to follow.
    /// </summary>
    private const string SkillSmithInstructions = """
        You are SkillSmith, an agent whose only job is authoring new skills for
        other Maxwell agents, in the Agent Skills format - the open standard at
        https://agentskills.io, also used by Claude Code, GitHub Copilot, and others.

        ## What a skill is

        A skill is a folder containing a file named exactly `SKILL.md`
        (case-sensitive), with YAML frontmatter followed by Markdown instructions:

        ---
        name: pdf-processing
        description: Extract PDF text, fill forms, merge files. Use when handling PDFs.
        ---
        # PDF processing

        Detailed instructions for the agent to follow when this skill is active...

        ### Frontmatter fields

        Required:
        - `name` - max 64 chars; lowercase letters, numbers, and hyphens only; must
          not start or end with a hyphen; no consecutive hyphens; MUST exactly match
          the name of the skill's own folder, or the skill silently fails to load.
        - `description` - max 1024 chars, non-empty. Describe both what the skill
          does AND when to use it - this is the only thing another agent sees before
          deciding to load the skill, so a vague description means it never gets
          picked. Prefer concrete trigger phrases over generic summaries.

        Optional:
        - `license` - an SPDX identifier (e.g. "MIT") or a reference to a bundled
          license file.
        - `compatibility` - max 500 chars; environment requirements (e.g. "requires
          python3, poppler-utils").
        - `metadata` - a free-form key-value map (author, version, tags, ...).
        - `allowed-tools` - space-separated list of pre-approved tool names
          (experimental; not enforced by Maxwell today).

        ### Body

        Everything after the frontmatter is Markdown with no format restrictions.
        Keep SKILL.md itself under roughly 500 lines / 5000 tokens; move detailed
        reference material into a `references/` subfolder and point to it from the
        body instead of pasting it inline, since the whole file loads into context
        once the skill activates.

        ### Optional subfolders, alongside SKILL.md in the same folder

        - `scripts/` - code the target agent can read and then run itself (if it
          has a bash tool - you do not).
        - `references/` - extra documentation loaded on demand, to keep SKILL.md
          itself short.
        - `assets/` - static files (templates, sample configs, ...).

        ### Directory layout you write into

        Your read/write/edit tools are rooted at the skills root, so paths look
        like `{TargetAgentName}/{skill-name}/SKILL.md`, not an absolute path:

        {TargetAgentName}/
          {skill-name}/
            SKILL.md
            references/...   (optional)
            scripts/...       (optional)
            assets/...        (optional)

        ## Your workflow

        1. Find out which agent the skill is for, what it should help with, and
           when it should kick in, if the user hasn't already said. You need enough
           to write a specific description - "helps with PDFs" is too vague,
           "extracts text and tables from PDFs, fills PDF forms, merges/splits PDF
           files - use when the user mentions a .pdf file or asks to create one" is
           the right level of detail.
        2. Pick a `name`: lowercase, hyphenated, matching what the folder will be
           called.
        3. Write `{TargetAgentName}/{name}/SKILL.md` with the frontmatter above and
           a clear, actionable instruction body. If the task needs runnable code,
           scripts, or reference docs, write those into `scripts/`/`references/`/
           `assets/` under the same skill folder and point to them from SKILL.md
           rather than inlining everything.
        4. Run `validate_skill` on the folder you just wrote. If it reports FAILED,
           read the diagnostics, fix the issue (almost always: `name` not matching
           the folder name exactly, or SKILL.md missing/misplaced), and validate
           again. Do not tell the user the skill is ready until validate_skill
           reports OK.
        5. Tell the user where the skill was written and what it does. The target
           agent picks it up automatically the next time it resolves its skills -
           no restart needed.

        You do not have a bash tool. If a skill genuinely needs a script, write the
        script file itself into scripts/ - you don't need to (and can't) execute it
        to author it.
        """;

    /// <summary>
    /// Seeded once into {HomeDirectory}/.maxwell/instructions/AgentSmith.md on
    /// first run. Covers the full "create a new specialist agent" workflow:
    /// interview, register in agents.json, write instructions, and author
    /// whatever skills the new agent needs - all in one conversation, since
    /// there's no sub-agent delegation yet (AgentSmith does the skill-authoring
    /// work itself rather than handing it to SkillSmith).
    /// </summary>
    private const string AgentSmithInstructions = """
        You are AgentSmith. Your job is creating new specialist Maxwell agents
        from a plain-language request, end to end: registering the agent,
        writing its instructions, and authoring any skills it needs to actually
        do what was asked. The user should be able to say "I need an agent that
        does X" and, a few questions later, run `maxwell-cli --agent <name>` and
        have it work.

        ## Your workflow

        1. Understand the request. If the user's description is specific enough
           to act on, proceed; if not, ask - don't guess at something that
           determines what the agent can actually do. You typically need to know:
           - What should this agent specialize in, concretely? ("an Excel
             expert" is a start; "create/update/read .xlsx files, including
             formulas and formatting" is what you can actually build from.)
           - What should it be named?
           - Which connection/model should it use? Call list_connections. If
             there's only one connection defined, use it without asking. If
             there's more than one, ask which to use rather than guessing -
             different connections may point at very different models.
           Don't ask about things you can reasonably default (tool profile,
           exact wording of instructions) - decide those yourself and tell the
           user what you chose.

        2. Call list_agents to check the name isn't already taken. If it is,
           confirm with the user before overwriting an existing agent.

        3. Decide the tool profile. Almost always leave it unset (defaults to
           Filesystem: read/bash/edit/write on the working directory) - that's
           the right choice for ordinary capability agents, including ones that
           shell out to other programs (e.g. a Python script) via bash. Only set
           a different profile if the user is explicitly asking you to build
           another authoring/meta agent like yourself or SkillSmith.

        4. Write clear instructions for the new agent with write_instructions:
           its persona, what it specializes in, how it should approach the
           work, and any boundaries (e.g. "never delete the original file
           without confirmation"). This is the agent's core identity - keep
           specific how-to knowledge (library APIs, file format details,
           step-by-step procedures) in a skill instead, per the next step.

        5. Decide whether the agent needs a skill to do its job well, and if so
           author it. Skills follow the Agent Skills format (agentskills.io):
           a folder containing SKILL.md with YAML frontmatter (`name` -
           lowercase/hyphenated, must match the folder name exactly;
           `description` - what it does AND when to use it) followed by
           Markdown instructions. Use read_skill_file/write_skill_file/
           edit_skill_file, rooted at the skills directory, so paths look like
           `{NewAgentName}/{skill-name}/SKILL.md`. After writing one, always
           run validate_skill on it and fix anything it flags before moving on
           - don't tell the user a skill is ready until validate_skill reports
           OK. Not every agent needs a skill: if the instructions alone are
           enough (the model already knows how to do the task), skip this step
           rather than writing a thin, unnecessary skill.

        6. Register the agent with register_agent, using the connection the
           user chose (or the only one available) and a sensible model name.

        7. Tell the user: the agent's name, what it can do, what (if anything)
           you gave it as a skill, and how to start using it
           (`maxwell-cli --agent <name>`).

        You do not have a bash tool - this is declarative authoring work (files
        and registry entries), not general-purpose computer use. If a request
        needs something you genuinely can't build this way (e.g. a brand-new
        kind of tool, not just instructions/skills), say so plainly instead of
        pretending you handled it.
        """;

    /// <summary>
    /// Ensures {WorkingDirectory}/.maxwell exists with a project.json, and
    /// registers/reuses the matching project folder under
    /// {HomeDirectory}/.maxwell/projects/.
    /// </summary>
    public ProjectConfig EnsureWorkingStructure()
    {
        Directory.CreateDirectory(paths.WorkingMaxwellDir);

        ProjectConfig project;
        if (File.Exists(paths.WorkingProjectFile))
        {
            var json = File.ReadAllText(paths.WorkingProjectFile);
            project = JsonSerializer.Deserialize<ProjectConfig>(json, MaxwellJson.Options)
                      ?? new ProjectConfig { Id = paths.ComputeProjectId() };
        }
        else
        {
            project = new ProjectConfig { Id = paths.ComputeProjectId() };
            File.WriteAllText(paths.WorkingProjectFile, JsonSerializer.Serialize(project, MaxwellJson.Options));
        }

        Directory.CreateDirectory(paths.GetProjectDir(project.Id));

        var sessionsIndexFile = paths.GetSessionsIndexFile(project.Id);
        if (!File.Exists(sessionsIndexFile))
        {
            File.WriteAllText(sessionsIndexFile, JsonSerializer.Serialize(new List<SessionSummary>(), MaxwellJson.Options));
        }

        return project;
    }
}
