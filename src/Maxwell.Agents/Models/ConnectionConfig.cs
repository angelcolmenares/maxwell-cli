namespace Maxwell.Agents.Models;

/// <summary>
/// One entry from {HomeDirectory}/.maxwell/connections.json, e.g.:
/// { "name": "llama-cpp", "clientType": "OpenAI", "options": { "apiKey": "no-key", "endpoint": "http://192.168.40.20:8080/v1" } }
/// </summary>
public sealed class ConnectionConfig
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Only "OpenAI" is implemented today. llama.cpp's server speaks the OpenAI
    /// chat-completions wire format, so it is reached through this client type by
    /// pointing <see cref="ConnectionOptions.Endpoint"/> at the local server.
    /// </summary>
    public string ClientType { get; set; } = "OpenAI";

    public ConnectionOptions Options { get; set; } = new();
}

public sealed class ConnectionOptions
{
    public string ApiKey { get; set; } = "no-key";

    public string Endpoint { get; set; } = string.Empty;
}
