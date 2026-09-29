using System.ComponentModel;

namespace Maxwell.Agents.Tools;

/// <summary>
/// "read_instructions"/"write_instructions" - reads or writes another agent's
/// system instructions file by agent name, not by raw path, so there's no
/// path-traversal surface at all here: the only input is a name, and
/// <see cref="MaxwellPaths.GetHomeAgentInstructionsFile"/> decides where that
/// maps to (always under {HomeDirectory}/.maxwell/instructions/).
/// </summary>
public sealed class InstructionsFileTool(MaxwellPaths paths)
{
    [Description("Read an agent's current system instructions, if any exist yet.")]
    public string ReadInstructions(
        [Description("The agent's name, exactly as it appears (or will appear) in agents.json.")]
        string agentName)
    {
        var file = paths.GetHomeAgentInstructionsFile(agentName);
        return File.Exists(file) ? File.ReadAllText(file) : $"(no instructions file yet for '{agentName}')";
    }

    [Description(
        "Create or completely overwrite an agent's system instructions - its persona, responsibilities, " +
        "and behavioral boundaries. This is general Markdown/plain text, not a skill: put the agent's " +
        "core identity and behavior here, and put specific how-to capabilities in skills instead.")]
    public string WriteInstructions(
        [Description("The agent's name, exactly as it appears (or will appear) in agents.json.")]
        string agentName,
        [Description("The full instructions text, replacing anything already there.")]
        string content)
    {
        if (string.IsNullOrWhiteSpace(agentName))
        {
            return "Error: agentName must not be empty.";
        }

        var file = paths.GetHomeAgentInstructionsFile(agentName);
        var existed = File.Exists(file);

        try
        {
            Directory.CreateDirectory(paths.HomeInstructionsDir);
            File.WriteAllText(file, content);
        }
        catch (Exception ex)
        {
            return $"Error: could not write instructions for '{agentName}': {ex.Message}";
        }

        return $"{(existed ? "Overwrote" : "Created")} instructions for '{agentName}' ({content.Length} chars).";
    }
}
