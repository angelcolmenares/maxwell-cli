using Maxwell.Agents.Plugins;

namespace Maxwell.Agents.Providers;

/// <summary>
/// Maps a <see cref="Models.ConnectionConfig.ClientType"/> string (e.g. "OpenAI",
/// "Anthropic") to the <see cref="IAgentProvider"/> that knows how to talk to it.
///
/// This is the one registry both the host and plugins write to: the host registers
/// its built-in providers first, then <see cref="PluginLoader"/> gives each loaded
/// plugin a chance to add (or deliberately override) entries via
/// <see cref="IPluginProviderRegistry"/>. "Last registration wins" is intentional -
/// it lets a plugin replace a built-in provider (e.g. swap in a hardened OpenAI
/// client) without Maxwell needing a separate override mechanism.
/// </summary>
public sealed class AgentProviderRegistry : IPluginProviderRegistry
{
    private readonly Dictionary<string, IAgentProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string clientType, IAgentProvider provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientType);
        ArgumentNullException.ThrowIfNull(provider);
        _providers[clientType] = provider;
    }

    public IAgentProvider Resolve(string clientType)
    {
        if (_providers.TryGetValue(clientType, out var provider))
        {
            return provider;
        }

        var known = _providers.Count == 0 ? "(none)" : string.Join(", ", _providers.Keys);
        throw new NotSupportedException(
            $"No agent provider is registered for clientType '{clientType}'. Known client types: {known}. " +
            "Either fix the connection's clientType in connections.json, or install/enable a plugin that " +
            "registers a provider for it.");
    }

    public IReadOnlyCollection<string> RegisteredClientTypes => _providers.Keys;
}
