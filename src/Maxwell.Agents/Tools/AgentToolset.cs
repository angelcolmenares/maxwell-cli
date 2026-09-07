using Microsoft.Extensions.AI;

namespace Maxwell.Agents.Tools;

/// <summary>
/// Wraps the built-in read/bash/edit/write tools as <see cref="AITool"/>s via
/// <see cref="AIFunctionFactory"/>, so they can be merged with whatever
/// per-agent tools <see cref="Providers.SkillLoader"/> discovers and handed to
/// <see cref="Providers.LlamaCppAgentProvider.CreateAgent"/>.
///
/// All four tools are rooted at a single directory (the working directory
/// Maxwell was launched from) so relative paths behave the same no matter what
/// the process's current directory happens to be.
/// </summary>
public static class AgentToolset
{
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
}
