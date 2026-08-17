using System.Text.Json;
using Maxwell.Agents.Models;

namespace Maxwell.Agents.Storage;

/// <summary>
/// Creates every directory/file Maxwell needs the first time it runs in a given
/// home or working directory, seeding sensible defaults (a llama.cpp connection
/// and a "Maxwell" agent) so a brand-new machine works out of the box.
/// </summary>
public sealed class MaxwellBootstrapper(MaxwellPaths paths)
{
    public const string DefaultAgentName = "Maxwell";
    private const string DefaultConnectionName = "llama-cpp";
    private const string DefaultEndpoint = "http://localhost:8080/v1";
    private const string DefaultModel = "gemma";

    /// <summary>Ensures {HomeDirectory}/.maxwell exists with default config files.</summary>
    public void EnsureHomeStructure()
    {
        Directory.CreateDirectory(paths.HomeMaxwellDir);
        Directory.CreateDirectory(paths.HomeSkillsDir);
        Directory.CreateDirectory(paths.HomeInstructionsDir);
        Directory.CreateDirectory(paths.ProjectsDir);
        Directory.CreateDirectory(paths.GetHomeAgentSkillsDir(DefaultAgentName));

        if (!File.Exists(paths.ConnectionsFile))
        {
            var defaults = new List<ConnectionConfig>
            {
                new()
                {
                    Name = DefaultConnectionName,
                    ClientType = "OpenAI",
                    Options = new ConnectionOptions { ApiKey = "no-key", Endpoint = DefaultEndpoint },
                },
            };
            File.WriteAllText(paths.ConnectionsFile, JsonSerializer.Serialize(defaults, MaxwellJson.Options));
        }

        if (!File.Exists(paths.AgentsFile))
        {
            var defaults = new List<AgentConfig>
            {
                new()
                {
                    Name = DefaultAgentName,
                    Connection = DefaultConnectionName,
                    Model = DefaultModel,
                    ClientType = "ChatCompletion",
                },
            };
            File.WriteAllText(paths.AgentsFile, JsonSerializer.Serialize(defaults, MaxwellJson.Options));
        }

        var defaultInstructionsFile = paths.GetHomeAgentInstructionsFile(DefaultAgentName);
        if (!File.Exists(defaultInstructionsFile))
        {
            File.WriteAllText(
                defaultInstructionsFile,
                "You are Maxwell, a helpful, direct AI assistant running locally via llama.cpp.\n");
        }
    }

    /// <summary>
    /// Ensures {WorkingDirectory}/.maxwell exists with a project.json, and
    /// registers/reuses the matching project folder under
    /// {HomeDirectory}/.maxwell/projects/.
    /// </summary>
    public ProjectConfig EnsureWorkingStructure()
    {
        Directory.CreateDirectory(paths.WorkingMaxwellDir);

        ProjectConfig project;
        if (File.Exists(paths.WorkingProjectFile))
        {
            var json = File.ReadAllText(paths.WorkingProjectFile);
            project = JsonSerializer.Deserialize<ProjectConfig>(json, MaxwellJson.Options)
                      ?? new ProjectConfig { Id = paths.ComputeProjectId() };
        }
        else
        {
            project = new ProjectConfig { Id = paths.ComputeProjectId() };
            File.WriteAllText(paths.WorkingProjectFile, JsonSerializer.Serialize(project, MaxwellJson.Options));
        }

        Directory.CreateDirectory(paths.GetProjectDir(project.Id));

        var sessionsIndexFile = paths.GetSessionsIndexFile(project.Id);
        if (!File.Exists(sessionsIndexFile))
        {
            File.WriteAllText(sessionsIndexFile, JsonSerializer.Serialize(new List<SessionSummary>(), MaxwellJson.Options));
        }

        return project;
    }
}
