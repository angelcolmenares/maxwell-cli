using Maxwell.Agents.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Maxwell.Agents.Providers;

/// <summary>
/// Builds an <see cref="AIAgent"/> for a given connection + agent config. Implement
/// this once per backend (OpenAI-compatible, Anthropic, Azure, Google, ...) and
/// register it in an <see cref="AgentProviderRegistry"/> keyed by
/// <see cref="ConnectionConfig.ClientType"/>; <see cref="MaxwellSessionService"/>
/// never needs to know which implementation it's talking to.
///
/// Built-in providers are registered at startup by <see cref="MaxwellSessionService"/>.
/// Plugins register additional ones via <see cref="Plugins.IMaxwellPlugin.ConfigureProviders"/>.
/// </summary>
public interface IAgentProvider
{
    AIAgent CreateAgent(ConnectionConfig connection, AgentConfig agentConfig, string instructions, IList<AITool>? tools = null);
}
