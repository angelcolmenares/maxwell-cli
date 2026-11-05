namespace Maxwell.Agents.Storage;

/// <summary>
/// Copies the skills that ship inside Maxwell.Agents (embedded from the BuiltInSkills folder) into
/// {HomeDirectory}/.maxwell/skills/{AgentName}/{skill-name}/, so an agent has them without anyone
/// writing them first. The embedded layout mirrors the destination:
/// BuiltInSkills/{AgentName}/{skill-name}/SKILL.md (+ optional scripts/, references/, assets/).
///
/// Seeding is a one-time gift, not a sync:
///  - Each skill is recorded in {HomeDirectory}/.maxwell/seeded-skills.txt once handled, so a skill
///    you delete stays deleted and an edited one is never overwritten by a newer built-in version.
///  - A skill folder that already exists is left alone (and counts as handled): your own skill with
///    the same name wins.
/// To get a fresh copy of a built-in skill, delete its line from seeded-skills.txt (and its folder).
/// </summary>
public sealed class BuiltInSkillSeeder(MaxwellPaths paths)
{
    private const string ResourcePrefix = "BuiltInSkills/";

    private string SeededFile => Path.Combine(paths.HomeMaxwellDir, "seeded-skills.txt");

    /// <returns>The "{AgentName}/{skill-name}" ids that were copied into the skills folder by this call.</returns>
    public IReadOnlyList<string> Seed()
    {
        var assembly = typeof(BuiltInSkillSeeder).Assembly;

        // Original resource name (what GetManifestResourceStream needs) paired with a forward-slash
        // form, since MSBuild can leave backslashes in RecursiveDir on Windows.
        var resources = assembly
            .GetManifestResourceNames()
            .Select(original => (Original: original, Normalized: original.Replace('\\', '/')))
            .Where(r => r.Normalized.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToList();

        var handled = File.Exists(SeededFile)
            ? File.ReadAllLines(SeededFile).Where(line => line.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var copied = new List<string>();
        var changed = false;

        // "BuiltInSkills/{agent}/{skill}/{path...}" grouped by "{agent}/{skill}".
        var bySkill = resources
            .Select(r => (r.Original, Parts: r.Normalized[ResourcePrefix.Length..].Split('/')))
            .Where(r => r.Parts.Length >= 3)
            .GroupBy(r => $"{r.Parts[0]}/{r.Parts[1]}");

        foreach (var skill in bySkill)
        {
            if (handled.Contains(skill.Key))
            {
                continue;
            }

            var skillDir = Path.Combine(paths.HomeSkillsDir, skill.First().Parts[0], skill.First().Parts[1]);
            if (!Directory.Exists(skillDir))
            {
                foreach (var (original, parts) in skill)
                {
                    var destination = Path.Combine([skillDir, .. parts.Skip(2)]);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                    using var source = assembly.GetManifestResourceStream(original)!;
                    using var target = File.Create(destination);
                    source.CopyTo(target);
                }

                copied.Add(skill.Key);
            }

            handled.Add(skill.Key);
            changed = true;
        }

        if (changed)
        {
            Directory.CreateDirectory(paths.HomeMaxwellDir);
            File.WriteAllLines(SeededFile, handled.Order(StringComparer.OrdinalIgnoreCase));
        }

        return copied;
    }
}
