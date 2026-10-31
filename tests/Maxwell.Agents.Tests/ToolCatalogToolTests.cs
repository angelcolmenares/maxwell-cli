using System.Text.Json;
using Maxwell.Agents.Models;
using Maxwell.Agents.Storage;
using Maxwell.Agents.Tools;
using Maxwell.Agents.Tools.Search;
using Microsoft.Extensions.AI;

namespace Maxwell.Agents.Tests;

/// <summary>
/// search_tools / call_tool through the real AIFunctionFactory-built task board tools. These are
/// the tests that exercise the assumptions the proxy rests on (JSON arguments bind to the tool's
/// parameters; the JSON schema has the usual shape), so if Microsoft.Extensions.AI behaves
/// differently in your version, this is where it shows.
/// </summary>
public sealed class ToolCatalogToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maxwell-tests-" + Guid.NewGuid().ToString("N"));

    private readonly TaskStore _store;
    private readonly ToolCatalogTool _catalog;
    private readonly IReadOnlyList<AIFunction> _functions;

    public ToolCatalogToolTests()
    {
        var paths = new MaxwellPaths(homeRoot: _root, workingRoot: _root);
        var config = new MaxwellConfigRepository(paths);
        config.SaveAgents([new AgentConfig { Name = "Worker", Connection = "c", Model = "m" }]);

        _store = new TaskStore(paths);
        _functions = AgentToolset.CreateTaskBoardTools(_store, config, "proj", "Orchestrator").OfType<AIFunction>().ToList();
        _catalog = new ToolCatalogTool(_functions, new LexicalToolSearchStrategy(), TaskBoardSearchHints.All);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Task_board_tools_keep_their_full_descriptions()
    {
        // Regression guard: passing a short `description:` to AIFunctionFactory.Create replaces the
        // detailed [Description] attribute, and that text is what search_tools shows the agent.
        var create = _functions.Single(f => f.Name == "create_task");

        Assert.Contains("self-contained", create.Description);
    }

    [Fact]
    public void The_meta_tools_keep_their_full_descriptions()
    {
        // Same regression guard as above: search_tools' [Description] tells agents to write the
        // query in English, and a short `description:` override would silently drop it.
        var discovery = AgentToolset.CreateToolDiscoveryTools(_functions, new LexicalToolSearchStrategy(), TaskBoardSearchHints.All)
            .OfType<AIFunction>()
            .ToList();

        Assert.Contains("English", discovery.Single(f => f.Name == "search_tools").Description);
        Assert.Contains("exact name", discovery.Single(f => f.Name == "call_tool").Description);
    }

    [Fact]
    public async Task SearchTools_shows_description_parameters_and_an_example()
    {
        var result = await _catalog.SearchTools("delegate a job to another agent");

        Assert.Contains("create_task", result);
        Assert.Contains("assignedAgent (string, required)", result);
        Assert.Contains("description (string, required)", result);
        Assert.Contains("call_tool(name=\"create_task\"", result);
    }

    [Fact]
    public async Task SearchTools_lists_available_tools_when_nothing_matches()
    {
        var result = await _catalog.SearchTools("weather forecast");

        Assert.Contains("No tool matched", result);
        Assert.Contains("English", result);
        Assert.Contains("create_task", result);
        Assert.Contains("fail_task", result);
    }

    [Fact]
    public async Task CallTool_runs_a_tool_with_arguments_as_the_model_sends_them()
    {
        // The model's tool call arrives as JSON; nested values deserialize to JsonElement.
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            """{"assignedAgent":"worker","description":"Clean sales_q3.xlsx and report row counts."}""");

        var result = await _catalog.CallTool("create_task", arguments);

        Assert.StartsWith("Created task-", result);
        var task = Assert.Single(_store.Load("proj"));
        Assert.Equal("Worker", task.AssignedAgent); // canonicalized from "worker"
        Assert.Equal("Orchestrator", task.CreatedBy);
        Assert.Equal(WorkItemStatus.Pending, task.Status);
    }

    [Fact]
    public async Task CallTool_explains_the_expected_parameters_when_arguments_are_wrong()
    {
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>("""{"description":"missing the agent"}""");

        var result = await _catalog.CallTool("create_task", arguments);

        Assert.StartsWith("Error calling 'create_task'", result);
        Assert.Contains("assignedAgent (string, required)", result);
        Assert.Empty(_store.Load("proj"));
    }

    [Fact]
    public async Task CallTool_rejects_an_unknown_tool_and_points_at_search()
    {
        var result = await _catalog.CallTool("does_not_exist", null);

        Assert.StartsWith("Error: no catalog tool named", result);
        Assert.Contains("search_tools", result);
    }

    [Fact]
    public async Task CallTool_is_not_a_way_to_reach_tools_outside_the_catalog()
    {
        // Only what was handed to the ToolCatalogTool is callable - nothing global.
        var narrow = new ToolCatalogTool(
            _functions.Where(f => f.Name == "list_tasks").ToList(),
            new LexicalToolSearchStrategy());

        var result = await narrow.CallTool("create_task", new Dictionary<string, object?>());

        Assert.StartsWith("Error: no catalog tool named", result);
    }
}
