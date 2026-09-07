using Maxwell.Agents.Providers;
using Microsoft.Extensions.AI;

namespace Maxwell.Agents.Plugins;

/// <summary>
/// Entry point a plugin assembly implements to hook into Maxwell. A plugin
/// assembly can contain more than one <see cref="IMaxwellPlugin"/> type
/// (uncommon but not forbidden) - <see cref="PluginLoader"/> instantiates every
/// public, non-abstract type that implements it.
///
/// Implementations must have a public parameterless constructor - Maxwell has no
/// DI container to satisfy plugin constructor dependencies, so plugins that need
/// configuration should read it themselves (e.g. from their own file under the
/// plugin's directory) rather than expecting it to be injected.
/// </summary>
public interface IMaxwellPlugin
{
    /// <summary>
    /// Stable identifier for this plugin, used in logs and `maxwell-cli plugins
    /// list`. Should match the "id" in plugin.json but callers must not assume
    /// that - always read it from here.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Called once at startup. Register zero or more <see cref="IAgentProvider"/>s,
    /// e.g. <c>providers.Register("Anthropic", new AnthropicAgentProvider())</c>.
    /// Implementations that don't add providers can leave this empty.
    /// </summary>
    void ConfigureProviders(IPluginProviderRegistry providers);

    /// <summary>
    /// Called once at startup. Register zero or more <see cref="AITool"/>s that
    /// should be available to every agent, alongside the built-in read/bash/edit/
    /// write tools and whatever the active agent's skills contribute.
    /// Implementations that don't add tools can leave this empty.
    /// </summary>
    void ConfigureTools(IPluginToolRegistry tools);
}

/// <summary>Narrow write-only view of <see cref="AgentProviderRegistry"/> handed to plugins.</summary>
public interface IPluginProviderRegistry
{
    void Register(string clientType, IAgentProvider provider);
}

/// <summary>Collects the <see cref="AITool"/>s a plugin contributes.</summary>
public interface IPluginToolRegistry
{
    void Register(AITool tool);

    void Register(IEnumerable<AITool> tools)
    {
        foreach (var tool in tools)
        {
            Register(tool);
        }
    }
}
