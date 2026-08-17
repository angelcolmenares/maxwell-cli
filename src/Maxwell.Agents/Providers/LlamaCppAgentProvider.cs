using AgentFrameworkToolkit.OpenAI;
using Maxwell.Agents.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Maxwell.Agents.Providers;

/// <summary>
/// The single supported AI provider for now (per the spec): llama.cpp's server,
/// reached through its OpenAI-compatible /v1 endpoint. Implemented via
/// AgentFrameworkToolkit.OpenAI's AgentFactory rather than raw Microsoft.Agents.AI
/// so that connection.options.endpoint is honoured (llama.cpp is not the real
/// OpenAI, so it must not hit api.openai.com).
///
/// Other clientType values (Anthropic, Azure OpenAI, Google, ...) are easy to add
/// later: swap in the matching AgentFrameworkToolkit.* package and branch on
/// ConnectionConfig.ClientType.
///
/// Separately, AgentConfig.ClientType controls the OpenAI wire protocol
/// (Chat Completions vs. Responses API) - see ParseClientType below.
/// </summary>
public sealed class LlamaCppAgentProvider
{
    public AIAgent CreateAgent(ConnectionConfig connection, AgentConfig agentConfig, string instructions, IList<AITool>? tools = null)
    {
        if (!string.Equals(connection.ClientType, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Connection '{connection.Name}' uses clientType '{connection.ClientType}', but only " +
                "'OpenAI' (used to reach llama.cpp's OpenAI-compatible endpoint) is implemented right now.");
        }

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
