using System.ComponentModel;
using System.Text;
using AgentSkillsDotNet;

namespace Maxwell.Agents.Tools;

/// <summary>
/// "validate_skill" - re-parses a skill folder through the same
/// <see cref="AgentSkillsFactory"/> that <see cref="Providers.SkillLoader"/> uses
/// at runtime, so the model gets a real pass/fail instead of guessing whether its
/// SKILL.md frontmatter is well-formed. This is deliberately the *same* loader
/// real agents use, not a separate hand-rolled validator - "validates" here means
/// "would actually load."
///
/// Only <see cref="AgentSkills.Skills"/> and <see cref="AgentSkills.ExcludedSkillsLog"/>
/// are relied on for the pass/fail verdict, since both are directly documented by
/// the package. <see cref="AgentSkill.GetValidationResult"/> exists too, but its
/// exact member shape isn't pinned down here - it's included via ToString() as
/// supplementary detail only, and a failure to read it doesn't flip the verdict.
/// </summary>
public sealed class SkillValidationTool(string skillsRootDirectory)
{
    [Description(
        "Validate a skill folder against the Agent Skills spec (agentskills.io) by loading it through " +
        "the real skill loader. Reports whether the skill loaded successfully and, if not, why. Run " +
        "this after writing a skill's SKILL.md and again after each fix, until it reports OK.")]
    public string Validate(
        [Description("Path to the skill's own folder (the one directly containing SKILL.md), relative to the skills root, e.g. 'Maxwell/pdf-processing'.")]
        string skillPath)
    {
        string fullSkillPath;
        try
        {
            fullSkillPath = ToolPaths.Resolve(skillsRootDirectory, skillPath);
        }
        catch (Exception ex)
        {
            return $"Error: invalid path '{skillPath}' ({ex.Message}).";
        }

        if (!Directory.Exists(fullSkillPath))
        {
            return $"Error: skill folder not found: {ToolPaths.Display(skillsRootDirectory, fullSkillPath)}";
        }

        var skillMdFile = Path.Combine(fullSkillPath, "SKILL.md");
        if (!File.Exists(skillMdFile))
        {
            return $"FAILED: {ToolPaths.Display(skillsRootDirectory, fullSkillPath)} has no SKILL.md. " +
                   "Every skill needs a file named exactly 'SKILL.md' (case-sensitive) directly inside its own folder.";
        }

        var expectedName = Path.GetFileName(fullSkillPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var parentDir = Path.GetDirectoryName(fullSkillPath)!;

        AgentSkills agentSkills;
        try
        {
            var factory = new AgentSkillsFactory();
            agentSkills = factory.GetAgentSkills(parentDir);
        }
        catch (Exception ex)
        {
            return $"Error: the Agent Skills loader threw while scanning '{ToolPaths.Display(skillsRootDirectory, parentDir)}': {ex.Message}";
        }

        var loaded = agentSkills.Skills.FirstOrDefault(s => string.Equals(s.Name, expectedName, StringComparison.Ordinal));
        var report = new StringBuilder();

        if (loaded is null)
        {
            report.AppendLine($"FAILED: no skill named '{expectedName}' loaded from {ToolPaths.Display(skillsRootDirectory, fullSkillPath)}.");

            var relevantLog = agentSkills.ExcludedSkillsLog
                .Where(line => line.Contains(expectedName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var logToShow = relevantLog.Count > 0 ? relevantLog : agentSkills.ExcludedSkillsLog;

            if (logToShow.Count > 0)
            {
                report.AppendLine("Excluded-skills log:");
                foreach (var line in logToShow)
                {
                    report.AppendLine($"  - {line}");
                }
            }
            else
            {
                report.AppendLine(
                    $"No diagnostics were logged either - double check that the frontmatter's 'name:' field " +
                    $"is exactly '{expectedName}' (must match the folder name, lowercase letters/numbers/hyphens " +
                    "only), and that SKILL.md sits directly under this folder, not nested deeper.");
            }

            return report.ToString();
        }

        report.AppendLine($"OK: '{loaded.Name}' loaded successfully.");
        report.AppendLine($"Description ({loaded.Description.Length} chars): {loaded.Description}");

        try
        {
            report.AppendLine($"Validation detail: {loaded.GetValidationResult()}");
        }
        catch (Exception ex)
        {
            report.AppendLine($"(Could not retrieve detailed validation result - not fatal, the skill did load: {ex.Message})");
        }

        return report.ToString();
    }
}
