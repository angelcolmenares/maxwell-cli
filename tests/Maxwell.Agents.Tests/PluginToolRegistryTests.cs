using Maxwell.Agents.Plugins;
using Maxwell.Agents.Tools.Search;
using Microsoft.Extensions.AI;

namespace Maxwell.Agents.Tests;

public class PluginToolRegistryTests
{
    private sealed class RecordingRegistry : IPluginToolRegistry
    {
        public List<(AITool Tool, ToolSearchHints Hints)> Entries { get; } = [];

        public void Register(AITool tool, ToolSearchHints hints) => Entries.Add((tool, hints));
    }

    private static AITool SomeTool(string name) => AIFunctionFactory.Create(() => "ok", name: name);

    [Fact]
    public void Register_with_keywords_becomes_hints_with_keywords_only()
    {
        IPluginToolRegistry registry = new RecordingRegistry();

        registry.Register(SomeTool("create_job"), ["dispatch", "hand off"]);

        var (_, hints) = Assert.Single(((RecordingRegistry)registry).Entries);
        Assert.Equal(new[] { "dispatch", "hand off" }, hints.Keywords);
        Assert.Empty(hints.Phrases);
    }

    [Fact]
    public void Register_with_just_a_tool_has_no_hints()
    {
        IPluginToolRegistry registry = new RecordingRegistry();

        registry.Register(SomeTool("t"));

        var (_, hints) = Assert.Single(((RecordingRegistry)registry).Entries);
        Assert.Empty(hints.Keywords);
        Assert.Empty(hints.Phrases);
    }

    [Fact]
    public void Register_many_registers_each_without_hints()
    {
        IPluginToolRegistry registry = new RecordingRegistry();

        registry.Register(new[] { SomeTool("a"), SomeTool("b") });

        Assert.Equal(2, ((RecordingRegistry)registry).Entries.Count);
    }

    [Fact]
    public void Register_with_hints_keeps_both_keywords_and_phrases()
    {
        IPluginToolRegistry registry = new RecordingRegistry();
        var hints = new ToolSearchHints { Keywords = ["dispatch"], Phrases = ["jobs i created"] };

        registry.Register(SomeTool("create_job"), hints);

        Assert.Equal(hints, Assert.Single(((RecordingRegistry)registry).Entries).Hints);
    }
}
