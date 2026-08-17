using AgentSkillsDotNet;
using Microsoft.Extensions.AI;

namespace Maxwell.Agents.Providers;

/// <summary>
/// Wraps AgentSkillsFactory to expose an agent's skills/{AgentName}/ folder(s) as
/// extra AITools plus extra instruction text, per the AgentFrameworkToolkit
/// "AgentSkills" pattern. This is best-effort: if a skills folder is empty,
/// missing, or the package's shape differs from what's coded here, callers should
/// treat a failure as "no skills available" rather than a fatal error.
/// </summary>
public sealed class SkillLoader
{
    public (IList<AITool> Tools, string? ExtraInstructions) Load(IReadOnlyList<string> skillDirectories)
    {
        var tools = new List<AITool>();
        var instructionParts = new List<string>();

        foreach (var dir in skillDirectories)
        {
            try
            {
                var factory = new AgentSkillsFactory();
                var skills = factory.GetAgentSkills(dir);
                tools.AddRange(skills.GetAsTools());

                var extra = skills.GetInstructions();
                if (!string.IsNullOrWhiteSpace(extra))
                {
                    instructionParts.Add(extra);
                }
            }
            catch (Exception)
            {
                // Best-effort: a malformed or unsupported skills folder should not
                // prevent the agent from starting without its skills.
            }
        }

        var extraInstructions = instructionParts.Count > 0 ? string.Join("\n\n", instructionParts) : null;
        return (tools, extraInstructions);
    }
}
