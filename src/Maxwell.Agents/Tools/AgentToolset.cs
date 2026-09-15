using Maxwell.Agents.Models;
using Maxwell.Agents.Storage;
using Microsoft.Extensions.AI;

namespace Maxwell.Agents.Tools;

/// <summary>Which built-in tool set an agent gets - see <see cref="AgentConfig.ToolProfile"/>.</summary>
public enum AgentToolProfile
{
    /// <summary>read/bash/edit/write rooted at the working directory. The default for ordinary agents.</summary>
    Filesystem,

    /// <summary>read/edit/write/validate_skill rooted at the skills directory, no bash. For <see cref="MaxwellBootstrapper.SkillSmithAgentName"/>-style skill-authoring agents.</summary>
    SkillAuthoring,
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
}
