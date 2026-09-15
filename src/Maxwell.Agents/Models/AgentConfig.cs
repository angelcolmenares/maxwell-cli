namespace Maxwell.Agents.Models;

/// <summary>
/// One entry from {HomeDirectory}/.maxwell/agents.json, e.g.:
/// { "name": "Maxwell", "connection": "llama-cpp", "model": "gemma" }
/// </summary>
public sealed class AgentConfig
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Name of the <see cref="ConnectionConfig"/> to use.</summary>
    public string Connection { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Which OpenAI wire protocol to use: "ChatCompletion" or "ResponsesApi".
    /// Optional - defaults to "ChatCompletion" in <see cref="Providers.OpenAIAgentProvider"/>
    /// when omitted or unrecognized. llama.cpp's server only supports the
    /// Chat Completions API today: pointing it at the Responses API fails with
    /// "llama.cpp does not support 'previous_response_id'." Set this to
    /// "ResponsesApi" only for backends that actually support it (e.g. real
    /// OpenAI, or a proxy that does) - that's also what's needed to get
    /// reasoning/"thinking" content back on streamed replies.
    /// </summary>
    public string? ClientType { get; set; }

    /// <summary>
    /// Which built-in tool set this agent gets: "Filesystem" (default) gives
    /// read/bash/edit/write rooted at the working directory; "SkillAuthoring"
    /// gives read/edit/write/validate_skill rooted at {HomeDirectory}/.maxwell/skills/
    /// instead, with no bash access - see <see cref="Tools.AgentToolset"/>. Skill-
    /// and plugin-contributed tools are layered on top regardless of profile.
    /// </summary>
    public string? ToolProfile { get; set; }
}
