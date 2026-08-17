using System.Text.Json;
using Maxwell.Agents.Models;

namespace Maxwell.Agents.Storage;

public sealed class MaxwellConfigRepository(MaxwellPaths paths)
{
    public IReadOnlyList<ConnectionConfig> LoadConnections()
    {
        if (!File.Exists(paths.ConnectionsFile))
        {
            return [];
        }

        var json = File.ReadAllText(paths.ConnectionsFile);
        return JsonSerializer.Deserialize<List<ConnectionConfig>>(json, MaxwellJson.Options) ?? [];
    }

    public IReadOnlyList<AgentConfig> LoadAgents()
    {
        if (!File.Exists(paths.AgentsFile))
        {
            return [];
        }

        var json = File.ReadAllText(paths.AgentsFile);
        return JsonSerializer.Deserialize<List<AgentConfig>>(json, MaxwellJson.Options) ?? [];
    }

    public AgentConfig GetAgentOrThrow(string agentName)
    {
        var agent = LoadAgents().FirstOrDefault(a => string.Equals(a.Name, agentName, StringComparison.OrdinalIgnoreCase));
        if (agent is null)
        {
            throw new InvalidOperationException(
                $"No agent named '{agentName}' found in {paths.AgentsFile}. " +
                "Add an entry there, e.g. {\"name\":\"MyAgent\",\"connection\":\"llama-cpp\",\"model\":\"gemma\"}.");
        }

        return agent;
    }

    public ConnectionConfig GetConnectionOrThrow(string connectionName)
    {
        var connection = LoadConnections().FirstOrDefault(c => string.Equals(c.Name, connectionName, StringComparison.OrdinalIgnoreCase));
        if (connection is null)
        {
            throw new InvalidOperationException(
                $"No connection named '{connectionName}' found in {paths.ConnectionsFile}.");
        }

        return connection;
    }

    /// <summary>
    /// Working-directory instructions override the Home ones; falls back to a
    /// generic instruction if neither file exists.
    /// </summary>
    public string LoadInstructions(string agentName)
    {
        var workingFile = paths.GetWorkingAgentInstructionsFile(agentName);
        if (File.Exists(workingFile))
        {
            return File.ReadAllText(workingFile);
        }

        var homeFile = paths.GetHomeAgentInstructionsFile(agentName);
        if (File.Exists(homeFile))
        {
            return File.ReadAllText(homeFile);
        }

        return $"You are {agentName}, a helpful AI assistant.";
    }

    /// <summary>
    /// Skill folders that exist for this agent, Home first then Working
    /// (Working folder is checked separately so callers can treat it as an
    /// override/addition rather than a silent replacement).
    /// </summary>
    public IReadOnlyList<string> GetSkillDirectories(string agentName)
    {
        var dirs = new List<string>();
        var homeDir = paths.GetHomeAgentSkillsDir(agentName);
        if (Directory.Exists(homeDir) && Directory.EnumerateFileSystemEntries(homeDir).Any())
        {
            dirs.Add(homeDir);
        }

        var workingDir = paths.GetWorkingAgentSkillsDir(agentName);
        if (Directory.Exists(workingDir) && Directory.EnumerateFileSystemEntries(workingDir).Any())
        {
            dirs.Add(workingDir);
        }

        return dirs;
    }
}
