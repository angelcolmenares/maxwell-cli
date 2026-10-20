using Maxwell.Agents.Models;
using Maxwell.Agents.Storage;
using Maxwell.Agents.Tools.Search;
using Microsoft.Extensions.AI;

namespace Maxwell.Agents.Tools;

/// <summary>Which built-in tool set an agent gets - see <see cref="AgentConfig.ToolProfile"/>.</summary>
public enum AgentToolProfile
{
    /// <summary>read/bash/edit/write rooted at the working directory. The default for ordinary agents.</summary>
    Filesystem,

    /// <summary>read/edit/write/validate_skill rooted at the skills directory, no bash. For <see cref="MaxwellBootstrapper.SkillSmithAgentName"/>-style skill-authoring agents.</summary>
    SkillAuthoring,

    /// <summary>Skill-authoring tools plus instructions read/write and agents.json registration, no bash. For <see cref="MaxwellBootstrapper.AgentSmithAgentName"/>-style agent-authoring agents.</summary>
    AgentAuthoring,
}

/// <summary>
/// Wraps the built-in tools as <see cref="AITool"/>s via <see cref="AIFunctionFactory"/>,
/// so they can be merged with whatever per-agent tools <see cref="Providers.SkillLoader"/>
/// discovers and handed to <see cref="Providers.IAgentProvider.CreateAgent"/>.
/// </summary>
public static class AgentToolset
{
    /// <summary>
    /// All four tools are rooted at a single directory (the working directory
    /// Maxwell was launched from) so relative paths behave the same no matter what
    /// the process's current directory happens to be.
    /// </summary>
    public static IReadOnlyList<AITool> CreateBuiltInTools(string rootDirectory)
    {
        var read = new ReadFileTool(rootDirectory);
        var bash = new BashTool(rootDirectory);
        var edit = new EditFileTool(rootDirectory);
        var write = new WriteFileTool(rootDirectory);

        return
        [
            AIFunctionFactory.Create(read.Read, name: "read",
                description: "Read file contents. Use this to examine files before editing."),
            AIFunctionFactory.Create(bash.Run, name: "bash",
                description: "Execute bash commands. Use this for file operations like ls, grep, find, etc."),
            AIFunctionFactory.Create(edit.Edit, name: "edit",
                description: "Make surgical edits to files. Use this for precise changes (old text must match exactly)."),
            AIFunctionFactory.Create(write.Write, name: "write",
                description: "Create or overwrite files. Use this for new files or complete rewrites."),
        ];
    }

    /// <summary>
    /// read/edit/write/validate_skill, all rooted at <paramref name="skillsRootDirectory"/>
    /// (normally {HomeDirectory}/.maxwell/skills/) instead of the working directory,
    /// and deliberately without bash: authoring a skill is pure file work
    /// (SKILL.md + optional scripts/references/assets), and an agent whose whole
    /// job is "write files describing what other agents should do" doesn't need
    /// shell access to do it, so it doesn't get any - narrower blast radius if
    /// something goes wrong (a bad prompt, a compromised skill request, ...).
    /// </summary>
    public static IReadOnlyList<AITool> CreateSkillAuthoringTools(string skillsRootDirectory)
    {
        var read = new ReadFileTool(skillsRootDirectory);
        var edit = new EditFileTool(skillsRootDirectory);
        var write = new WriteFileTool(skillsRootDirectory);
        var validate = new SkillValidationTool(skillsRootDirectory);

        return
        [
            AIFunctionFactory.Create(read.Read, name: "read",
                description: "Read file contents. Use this to examine an existing skill's files before editing them."),
            AIFunctionFactory.Create(edit.Edit, name: "edit",
                description: "Make surgical edits to a skill's files (old text must match exactly)."),
            AIFunctionFactory.Create(write.Write, name: "write",
                description: "Create or overwrite a skill's files: SKILL.md, and optionally scripts/, references/, assets/."),
            AIFunctionFactory.Create(validate.Validate, name: "validate_skill",
                description: "Validate a skill folder against the Agent Skills spec. Always run this after writing or editing SKILL.md."),
        ];
    }

    /// <summary>
    /// Everything <see cref="CreateSkillAuthoringTools"/> gives (renamed to
    /// *_skill_file to avoid colliding with the instructions read/write tools
    /// below), plus instructions read/write and agents.json registration - the
    /// full set an agent that creates *other agents* needs. Still no bash, for
    /// the same reason as SkillAuthoring: this is declarative authoring work
    /// (files + JSON registry entries), not general-purpose computer use.
    /// </summary>
    public static IReadOnlyList<AITool> CreateAgentAuthoringTools(MaxwellPaths paths, MaxwellConfigRepository config)
    {
        var skillRead = new ReadFileTool(paths.HomeSkillsDir);
        var skillEdit = new EditFileTool(paths.HomeSkillsDir);
        var skillWrite = new WriteFileTool(paths.HomeSkillsDir);
        var validate = new SkillValidationTool(paths.HomeSkillsDir);
        var instructions = new InstructionsFileTool(paths);
        var registry = new AgentRegistryTool(config);

        return
        [
            AIFunctionFactory.Create(skillRead.Read, name: "read_skill_file",
                description: "Read a file inside a skill folder. Use this to examine an existing skill before editing it."),
            AIFunctionFactory.Create(skillEdit.Edit, name: "edit_skill_file",
                description: "Make surgical edits to a skill's files (old text must match exactly)."),
            AIFunctionFactory.Create(skillWrite.Write, name: "write_skill_file",
                description: "Create or overwrite a skill's files: SKILL.md, and optionally scripts/, references/, assets/."),
            AIFunctionFactory.Create(validate.Validate, name: "validate_skill",
                description: "Validate a skill folder against the Agent Skills spec. Always run this after writing or editing a skill's SKILL.md."),
            AIFunctionFactory.Create(instructions.ReadInstructions, name: "read_instructions",
                description: "Read an agent's current system instructions, if any exist yet."),
            AIFunctionFactory.Create(instructions.WriteInstructions, name: "write_instructions",
                description: "Create or overwrite an agent's system instructions (persona, responsibilities, boundaries)."),
            AIFunctionFactory.Create(registry.ListConnections, name: "list_connections",
                description: "List available connections from connections.json, so you know what to register an agent against."),
            AIFunctionFactory.Create(registry.ListAgents, name: "list_agents",
                description: "List agents already defined in agents.json."),
            AIFunctionFactory.Create(registry.Register, name: "register_agent",
                description: "Add or update an agent's entry in agents.json."),
        ];
    }

    /// <summary>
    /// The task board tools (create_task, list_tasks, claim_task, complete_task,
    /// fail_task). Unlike the profile-specific sets above, these are merged into
    /// *every* agent's tools regardless of <see cref="AgentToolProfile"/> -
    /// coordination is cross-cutting: any agent might delegate work or receive it.
    /// </summary>
    public static IReadOnlyList<AITool> CreateTaskBoardTools(
        TaskStore store, MaxwellConfigRepository config, string projectId, string agentName)
    {
        var board = new TaskBoardTool(store, config, projectId, agentName);

        // No description: arguments here, deliberately. Unlike the file tools above, these methods
        // carry detailed [Description] attributes, which AIFunctionFactory uses when none is passed -
        // and since these tools sit behind search_tools, that text is what gets searched and what
        // the agent reads to learn how to call them. (A short override here would hide it.)
        return
        [
            AIFunctionFactory.Create(board.CreateTask, name: "create_task"),
            AIFunctionFactory.Create(board.ListTasks, name: "list_tasks"),
            AIFunctionFactory.Create(board.ClaimTask, name: "claim_task"),
            AIFunctionFactory.Create(board.CompleteTask, name: "complete_task"),
            AIFunctionFactory.Create(board.FailTask, name: "fail_task"),
        ];
    }

    /// <summary>
    /// Extra search terms for the task board tools - synonyms for what each does that its name and
    /// description don't contain, so search_tools("delegate a job") finds create_task. Keyed by tool
    /// name; see <see cref="Search.LexicalToolSearchStrategy"/> for how keywords weigh in.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> TaskBoardKeywords { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["create_task"] = ["delegate", "dispatch", "hand off", "assign work", "schedule", "enqueue", "todo", "subtask", "workflow", "new job"],
            ["list_tasks"] = ["queue", "inbox", "backlog", "pending", "progress", "status", "check results"],
            ["claim_task"] = ["start", "begin", "start working", "begin working", "accept", "pick up", "take on"],
            ["complete_task"] = ["finish", "finished", "done", "submit", "report result", "close"],
            ["fail_task"] = ["abort", "give up", "blocked", "unable", "cannot", "error"],
        };

    /// <summary>
    /// search_tools + call_tool over <paramref name="catalog"/>: the two always-loaded
    /// entry points to every tool that is *not* loaded by default. See <see cref="ToolCatalogTool"/>.
    /// </summary>
    public static IReadOnlyList<AITool> CreateToolDiscoveryTools(
        IReadOnlyList<AIFunction> catalog,
        IToolSearchStrategy searchStrategy,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? keywords = null)
    {
        var discovery = new ToolCatalogTool(catalog, searchStrategy, keywords);

        return
        [
            AIFunctionFactory.Create(discovery.SearchTools, name: "search_tools",
                description: "Find additional tools by describing what you need. Returns their descriptions and parameters."),
            AIFunctionFactory.Create(discovery.CallTool, name: "call_tool",
                description: "Run an additional tool found with search_tools, by name, with an arguments object."),
        ];
    }
}
