using System.Text.RegularExpressions;
using Maxwell.Agents.Storage;

namespace Maxwell.Agents.Tests;

public sealed class BuiltInSkillSeederTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maxwell-seed-" + Guid.NewGuid().ToString("N"));

    private readonly MaxwellPaths _paths;

    public BuiltInSkillSeederTests() => _paths = new MaxwellPaths(homeRoot: _root, workingRoot: _root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string SkillFile(string agent, string skill) => Path.Combine(_paths.HomeSkillsDir, agent, skill, "SKILL.md");

    [Fact]
    public void Seeds_the_web_browser_skill_for_the_default_agent()
    {
        var copied = new BuiltInSkillSeeder(_paths).Seed();

        Assert.Contains("Maxwell/web-browser", copied);
        Assert.True(File.Exists(SkillFile("Maxwell", "web-browser")));
    }

    [Fact]
    public void A_deleted_skill_stays_deleted()
    {
        var seeder = new BuiltInSkillSeeder(_paths);
        seeder.Seed();
        Directory.Delete(Path.GetDirectoryName(SkillFile("Maxwell", "web-browser"))!, recursive: true);

        var second = seeder.Seed();

        Assert.Empty(second);
        Assert.True(!File.Exists(SkillFile("Maxwell", "web-browser")), "seeding is one-time; it must not bring back a skill the user removed");
    }

    [Fact]
    public void Never_overwrites_a_skill_that_already_exists()
    {
        var file = SkillFile("Maxwell", "web-browser");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "my own version");

        var copied = new BuiltInSkillSeeder(_paths).Seed();

        Assert.True(!copied.Contains("Maxwell/web-browser"), "the user's own skill with that name must win");
        Assert.Equal("my own version", File.ReadAllText(file));
        Assert.Contains("Maxwell/web-browser", File.ReadAllLines(Path.Combine(_paths.HomeMaxwellDir, "seeded-skills.txt")));
    }

    [Fact]
    public void Seeding_twice_changes_nothing_the_second_time()
    {
        var seeder = new BuiltInSkillSeeder(_paths);
        seeder.Seed();
        File.WriteAllText(SkillFile("Maxwell", "web-browser"), "edited after seeding");

        Assert.Empty(seeder.Seed());
        Assert.Equal("edited after seeding", File.ReadAllText(SkillFile("Maxwell", "web-browser")));
    }

    // ---- the shipped skills themselves ----

    private IEnumerable<(string Folder, string Name, string Description, string Body, string Directory)> SeededSkills()
    {
        new BuiltInSkillSeeder(_paths).Seed();

        foreach (var file in Directory.EnumerateFiles(_paths.HomeSkillsDir, "SKILL.md", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var match = Regex.Match(text, @"\A---\r?\n(.*?)\r?\n---\r?\n(.*)\z", RegexOptions.Singleline);
            Assert.True(match.Success, $"{file} has no YAML frontmatter");

            var fields = match.Groups[1].Value
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r').Split(": ", 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1]);

            var directory = Path.GetDirectoryName(file)!;
            yield return (Path.GetFileName(directory), fields["name"], fields["description"], match.Groups[2].Value, directory);
        }
    }

    [Fact]
    public void Every_built_in_skill_meets_the_agent_skills_spec()
    {
        var skills = SeededSkills().ToList();
        Assert.NotEmpty(skills);

        foreach (var (folder, name, description, _, _) in skills)
        {
            Assert.Equal(folder, name); // the name must match the folder, or the skill silently fails to load
            Assert.True(Regex.IsMatch(name, "^[a-z0-9]+(-[a-z0-9]+)*$"), $"'{name}': lowercase letters, digits and single hyphens only");
            Assert.True(name.Length <= 64, $"'{name}' is longer than 64 characters");
            Assert.True(description.Length is > 0 and <= 1024, $"'{name}': description must be 1-1024 characters");
            Assert.True(!description.Contains(": ") && !description.Contains(" #"), $"'{name}': a plain YAML description can't contain ': ' or ' #'");
        }
    }

    // Commands playwright-cli 0.1.23 lists in `playwright-cli --help`. If the skill ever teaches a
    // command that isn't here, a model will try it and fail; this keeps the doc honest as it's edited.
    private static readonly HashSet<string> RealPlaywrightCliCommands = new(StringComparer.Ordinal)
    {
        "open", "attach", "close", "detach", "goto", "type", "click", "dblclick", "fill", "drag", "drop", "hover",
        "select", "upload", "check", "uncheck", "snapshot", "find", "eval", "dialog-accept", "dialog-dismiss", "resize",
        "delete-data", "go-back", "go-forward", "reload", "press", "keydown", "keyup", "screenshot", "pdf",
        "tab-list", "tab-new", "tab-close", "tab-select", "state-load", "state-save", "console", "requests", "request",
        "route", "run-code", "show", "install", "install-browser", "list", "close-all", "kill-all",
    };

    [Fact]
    public void Built_in_skills_only_use_commands_playwright_cli_really_has()
    {
        var skills = SeededSkills().ToList();
        var used = new List<string>();

        foreach (var (_, name, _, body, _) in skills)
        {
            // "playwright-cli [-s=name] <command>" - the command is the first plain word after it.
            foreach (Match match in Regex.Matches(body, @"playwright-cli(?: -s=\S+)? ([a-z][a-z-]*)"))
            {
                var command = match.Groups[1].Value;
                used.Add(command);
                Assert.Contains(command, RealPlaywrightCliCommands);
            }
        }

        // Both browser-based skills must actually have been checked, not silently skipped.
        Assert.Contains("web-browser", skills.Select(s => s.Name));
        Assert.Contains("web-search", skills.Select(s => s.Name));
        Assert.Contains("eval", used); // web-search's DuckDuckGo step
        Assert.Contains("snapshot", used);
    }

    [Fact]
    public void Scripts_a_skill_refers_to_are_copied_with_it()
    {
        foreach (var (_, name, _, body, directory) in SeededSkills())
        {
            foreach (Match match in Regex.Matches(body, @"scripts/([\w.-]+\.\w+)"))
            {
                var script = Path.Combine(directory, "scripts", match.Groups[1].Value);
                Assert.True(File.Exists(script), $"'{name}' refers to scripts/{match.Groups[1].Value}, which was not copied");
            }
        }
    }

    [Fact]
    public void Seeds_the_web_search_skill_with_its_scripts()
    {
        var copied = new BuiltInSkillSeeder(_paths).Seed();

        Assert.Contains("Maxwell/web-search", copied);
        var directory = Path.GetDirectoryName(SkillFile("Maxwell", "web-search"))!;
        Assert.True(File.Exists(Path.Combine(directory, "scripts", "searxng-search.mjs")));
        Assert.True(File.Exists(Path.Combine(directory, "scripts", "page-text.mjs")));
    }

    [Fact]
    public void A_skill_added_in_a_later_version_is_seeded_next_to_ones_already_handled()
    {
        // An install from before web-search existed: only web-browser was ever seeded.
        Directory.CreateDirectory(_paths.HomeMaxwellDir);
        File.WriteAllLines(Path.Combine(_paths.HomeMaxwellDir, "seeded-skills.txt"), ["Maxwell/web-browser"]);

        var copied = new BuiltInSkillSeeder(_paths).Seed();

        Assert.Equal(new[] { "Maxwell/web-search" }, copied);
        Assert.True(!File.Exists(SkillFile("Maxwell", "web-browser")), "an already-handled skill must not be re-created");
    }
}
