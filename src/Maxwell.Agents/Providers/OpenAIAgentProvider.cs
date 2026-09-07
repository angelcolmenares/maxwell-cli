using AgentFrameworkToolkit.OpenAI;
using Maxwell.Agents.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Maxwell.Agents.Providers;

/// <summary>
/// Built-in provider for connections whose <see cref="ConnectionConfig.ClientType"/>
/// is "OpenAI" - i.e. anything reached through an OpenAI-compatible /v1 endpoint,
/// which today means llama.cpp's server. Implemented via
/// AgentFrameworkToolkit.OpenAI's AgentFactory rather than raw Microsoft.Agents.AI
/// so that connection.options.endpoint is honoured (llama.cpp is not the real
/// OpenAI, so it must not hit api.openai.com).
///
/// This is registered by <see cref="MaxwellSessionService"/> in the
/// <see cref="AgentProviderRegistry"/> under the "OpenAI" client type. Other
/// client types (Anthropic, Azure OpenAI, Google, ...) no longer need to be added
/// here - they're implemented as their own <see cref="IAgentProvider"/>, either
/// built into Maxwell.Agents or registered by a plugin at startup.
///
/// Separately, AgentConfig.ClientType controls the OpenAI wire protocol
/// (Chat Completions vs. Responses API) - see ParseClientType below. This is
/// unrelated to ConnectionConfig.ClientType above and is specific to this provider.
/// </summary>
public sealed class OpenAIAgentProvider : IAgentProvider
{
    public AIAgent CreateAgent(ConnectionConfig connection, AgentConfig agentConfig, string instructions, IList<AITool>? tools = null)
    {
        if (string.IsNullOrWhiteSpace(connection.Options.Endpoint))
        {
            throw new InvalidOperationException($"Connection '{connection.Name}' is missing options.endpoint.");
        }

        var openAiConnection = new OpenAIConnection
        {
            ApiKey = string.IsNullOrWhiteSpace(connection.Options.ApiKey) ? "no-key" : connection.Options.ApiKey,
            Endpoint = connection.Options.Endpoint,
        };

        var factory = new OpenAIAgentFactory(openAiConnection);

        var options = new AgentOptions
        {
            Model = agentConfig.Model,
            Instructions = instructions,
            Tools = tools ?? [],
            ClientType = ParseClientType(agentConfig.ClientType),
        };

        return factory.CreateAgent(options);
    }

    /// <summary>
    /// Defaults to ChatCompletion, which is what llama.cpp's server supports.
    /// Its Responses-API-shaped endpoint rejects the "previous_response_id" field
    /// the framework sends when ClientType.ResponsesApi is used
    /// ("llama.cpp does not support 'previous_response_id'"), so ResponsesApi is
    /// opt-in per agent via agents.json's "clientType" field, for backends that
    /// actually support it.
    /// </summary>
    private static ClientType ParseClientType(string? value) =>
        Enum.TryParse<ClientType>(value, ignoreCase: true, out var parsed) ? parsed : ClientType.ChatClient;
}
