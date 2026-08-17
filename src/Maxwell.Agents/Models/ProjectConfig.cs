namespace Maxwell.Agents.Models;

/// <summary>
/// Content of {WorkingDirectory}/.maxwell/project.json. Identifies which
/// subfolder of {HomeDirectory}/.maxwell/projects/ stores this project's
/// sessions and chats.
/// </summary>
public sealed class ProjectConfig
{
    public string Id { get; set; } = string.Empty;
}
