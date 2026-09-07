using System.Text;

namespace Maxwell.Agents;

/// <summary>
/// Resolves every path used by Maxwell, per the layout:
///
/// {HomeDirectory}/.maxwell/
///     connections.json
///     agents.json
///     skills/{AgentName}/
///     instructions/{AgentName}.md
///     projects/{ProjectId}/
///         sessions.json
///         {sessionId}.json
///
/// {WorkingDirectory}/.maxwell/
///     project.json
///     skills/{AgentName}/          (optional overrides, merged on top of Home)
///     instructions/{AgentName}.md  (optional overrides, replace the Home version)
/// </summary>
public sealed class MaxwellPaths
{
    public string HomeRoot { get; }
    public string WorkingRoot { get; }

    public MaxwellPaths(string? homeRoot = null, string? workingRoot = null)
    {
        HomeRoot = homeRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        WorkingRoot = Path.GetFullPath(workingRoot ?? Directory.GetCurrentDirectory());
    }

    public string HomeMaxwellDir => Path.Combine(HomeRoot, ".maxwell");
    public string WorkingMaxwellDir => Path.Combine(WorkingRoot, ".maxwell");

    public string ConnectionsFile => Path.Combine(HomeMaxwellDir, "connections.json");
    public string AgentsFile => Path.Combine(HomeMaxwellDir, "agents.json");

    public string HomeSkillsDir => Path.Combine(HomeMaxwellDir, "skills");
    public string HomeInstructionsDir => Path.Combine(HomeMaxwellDir, "instructions");
    public string ProjectsDir => Path.Combine(HomeMaxwellDir, "projects");

    /// <summary>
    /// {HomeDirectory}/.maxwell/plugins/{pluginFolder}/plugin.json + entry assembly.
    /// Scanned by <see cref="Plugins.PluginLoader"/>; there is no working-directory
    /// equivalent (unlike skills/instructions) - plugins are host-wide, not per-project.
    /// </summary>
    public string PluginsDir => Path.Combine(HomeMaxwellDir, "plugins");

    public string WorkingProjectFile => Path.Combine(WorkingMaxwellDir, "project.json");
    public string WorkingSkillsDir => Path.Combine(WorkingMaxwellDir, "skills");
    public string WorkingInstructionsDir => Path.Combine(WorkingMaxwellDir, "instructions");

    public string GetHomeAgentSkillsDir(string agentName) => Path.Combine(HomeSkillsDir, agentName);
    public string GetWorkingAgentSkillsDir(string agentName) => Path.Combine(WorkingSkillsDir, agentName);

    public string GetHomeAgentInstructionsFile(string agentName) => Path.Combine(HomeInstructionsDir, $"{agentName}.md");
    public string GetWorkingAgentInstructionsFile(string agentName) => Path.Combine(WorkingInstructionsDir, $"{agentName}.md");

    public string GetProjectDir(string projectId) => Path.Combine(ProjectsDir, projectId);
    public string GetSessionsIndexFile(string projectId) => Path.Combine(GetProjectDir(projectId), "sessions.json");
    public string GetSessionFile(string projectId, string sessionId) => Path.Combine(GetProjectDir(projectId), $"{sessionId}.json");

    /// <summary>
    /// Derives a stable, filesystem-safe project id from the working directory's
    /// full path (the spec calls this "TheFullNameOfTheFolder"). Using the full
    /// path - not just the leaf folder name - avoids collisions between
    /// same-named folders that live in different locations.
    /// </summary>
    public string ComputeProjectId()
    {
        var full = WorkingRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var builder = new StringBuilder(full.Length);
        foreach (var c in full)
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '_' : c);
        }

        return builder.ToString().Trim('_');
    }
}
