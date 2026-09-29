using System.ComponentModel;
using System.Text;
using Maxwell.Agents.Models;
using Maxwell.Agents.Storage;

namespace Maxwell.Agents.Tools;

/// <summary>
/// "list_connections"/"list_agents"/"register_agent" - lets the model check
/// what already exists before deciding what to write, then commit a new or
/// updated agent through <see cref="MaxwellConfigRepository.UpsertAgent"/>
/// rather than being handed a raw read/write over agents.json.
/// </summary>
public sealed class AgentRegistryTool(MaxwellConfigRepository config)
{
    [Description(
        "List every connection defined in connections.json (name and clientType only, never secrets), " +
        "so you know what's available before registering an agent against one.")]
    public string ListConnections()
    {
        var connections = config.LoadConnections();
        if (connections.Count == 0)
        {
            return "No connections are defined yet - ask the user to add one to connections.json first.";
        }

        var builder = new StringBuilder();
        foreach (var connection in connections)
        {
            builder.AppendLine($"- {connection.Name} (clientType: {connection.ClientType})");
        }

        return builder.ToString();
    }

    [Description("List every agent already defined in agents.json, with its connection, model, and tool profile.")]
    public string ListAgents()
    {
        var agents = config.LoadAgents();
        if (agents.Count == 0)
        {
            return "No agents are defined yet.";
        }

        var builder = new StringBuilder();
        foreach (var agent in agents)
        {
            builder.AppendLine($"- {agent.Name} (connection: {agent.Connection}, model: {agent.Model}, toolProfile: {agent.ToolProfile ?? "Filesystem"})");
        }

        return builder.ToString();
    }

    [Description(
        "Add a new agent to agents.json, or update the existing one if an agent with this name already " +
        "exists. Call list_connections first if you're not sure which connection to use, and list_agents " +
        "first to check whether this name is already taken before you overwrite it.")]
    public string Register(
        [Description("The agent's name, as it will appear in agents.json and be passed to --agent.")]
        string name,
        [Description("Name of an existing connection from connections.json - see list_connections.")]
        string connection,
        [Description("Model name/id to request from that connection.")]
        string model,
        [Description("\"ChatCompletion\" (default) or \"ResponsesApi\" - which OpenAI wire protocol to use. Leave unset unless you know the backend needs ResponsesApi.")]
        string? clientType = null,
        [Description("\"Filesystem\" (default: read/bash/edit/write), \"SkillAuthoring\", or \"AgentAuthoring\" - which built-in tool set this agent gets. Leave unset for an ordinary capability agent.")]
        string? toolProfile = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Error: name must not be empty.";
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return "Error: model must not be empty.";
        }

        var connections = config.LoadConnections();
        var matchedConnection = connections.FirstOrDefault(c => string.Equals(c.Name, connection, StringComparison.OrdinalIgnoreCase));
        if (matchedConnection is null)
        {
            var available = connections.Count == 0 ? "(none defined)" : string.Join(", ", connections.Select(c => c.Name));
            return $"Error: no connection named '{connection}'. Available connections: {available}. " +
                   "Call list_connections for details, or ask the user which one to use.";
        }

        if (toolProfile is not null && !Enum.TryParse<AgentToolProfile>(toolProfile, ignoreCase: true, out _))
        {
            return $"Error: unrecognized toolProfile '{toolProfile}'. Use one of: {string.Join(", ", Enum.GetNames<AgentToolProfile>())}.";
        }

        var existed = config.LoadAgents().Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

        config.UpsertAgent(new AgentConfig
        {
            Name = name,
            Connection = matchedConnection.Name,
            Model = model,
            ClientType = clientType,
            ToolProfile = toolProfile,
        });

        return $"{(existed ? "Updated" : "Registered")} agent '{name}' (connection: {matchedConnection.Name}, model: {model}" +
               $"{(toolProfile is not null ? $", toolProfile: {toolProfile}" : string.Empty)}).";
    }
}
