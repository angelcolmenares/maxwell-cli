using System.ComponentModel;
using System.Reflection;
using Maxwell.Agents.Tools;
using Maxwell.Agents.Tools.Search;

namespace Maxwell.Agents.Tests;

/// <summary>
/// "Golden queries": what an agent might type into search_tools, and which task board tool should
/// come out on top. The documents use the real TaskBoardTool [Description] text and the real
/// TaskBoardSearchHints, so editing a description or a hint that breaks a query shows up here
/// rather than as a model that mysteriously stops finding a tool.
/// </summary>
public class LexicalToolSearchStrategyTests
{
    private static readonly IReadOnlyList<ToolSearchDocument> TaskBoard = BuildTaskBoardDocuments();

    private static readonly LexicalToolSearchStrategy Strategy = new();

    private static List<ToolSearchDocument> BuildTaskBoardDocuments()
    {
        // tool name -> TaskBoardTool method. AIFunctionFactory takes a tool's description from the
        // method's [Description] when none is passed, which is exactly what AgentToolset relies on.
        var methods = new Dictionary<string, string>
        {
            ["create_task"] = nameof(TaskBoardTool.CreateTask),
            ["list_tasks"] = nameof(TaskBoardTool.ListTasks),
            ["claim_task"] = nameof(TaskBoardTool.ClaimTask),
            ["complete_task"] = nameof(TaskBoardTool.CompleteTask),
            ["fail_task"] = nameof(TaskBoardTool.FailTask),
        };

        return methods
            .Select(pair => new ToolSearchDocument(
                pair.Key,
                typeof(TaskBoardTool).GetMethod(pair.Value)!.GetCustomAttribute<DescriptionAttribute>()!.Description,
                TaskBoardSearchHints.All[pair.Key].Keywords,
                TaskBoardSearchHints.All[pair.Key].Phrases))
            .ToList();
    }

    private static List<string> Names(IEnumerable<ToolSearchHit> hits) => hits.Select(h => h.Name).ToList();

    [Theory]
    [InlineData("assign work to another agent", "create_task")]
    [InlineData("delegate a job", "create_task")]
    [InlineData("hand off this to another agent", "create_task")]
    [InlineData("delegating", "create_task")]
    [InlineData("dispatch work", "create_task")]
    [InlineData("create a task", "create_task")]
    [InlineData("create a task for the Excel agent", "create_task")]
    [InlineData("I want to create a new task for Maxwell", "create_task")]
    [InlineData("what is waiting in my queue", "list_tasks")]
    [InlineData("check the results of what I delegated", "list_tasks")]
    [InlineData("tasks I created", "list_tasks")]
    [InlineData("show tasks I created", "list_tasks")]
    [InlineData("list the tasks I created", "list_tasks")]
    [InlineData("my tasks", "list_tasks")]
    [InlineData("what did I create", "list_tasks")]
    [InlineData("tasks assigned to me", "list_tasks")]
    [InlineData("what tasks are assigned to me", "list_tasks")]
    [InlineData("I finished the work, submit the result", "complete_task")]
    [InlineData("mark it done", "complete_task")]
    [InlineData("I cannot do this, give up", "fail_task")]
    [InlineData("start working on a task", "claim_task")]
    public void Finds_the_right_task_board_tool(string query, string expectedTop)
    {
        var hits = Strategy.Search(query, TaskBoard, maxResults: 5);

        Assert.NotEmpty(hits);
        Assert.Equal(expectedTop, hits[0].Name);
    }

    [Fact]
    public void Wording_and_tense_decide_between_listing_tasks_and_creating_one()
    {
        // Same content words to a stemmer ("task", "creat"); only the literal phrases tell them apart.
        Assert.Equal("list_tasks", Strategy.Search("tasks I created", TaskBoard, 5)[0].Name);
        Assert.Equal("create_task", Strategy.Search("create a task", TaskBoard, 5)[0].Name);
    }

    [Fact]
    public void A_clear_create_request_does_not_drag_in_list_tasks()
    {
        var hit = Assert.Single(Strategy.Search("create a task", TaskBoard, 5));

        Assert.Equal("create_task", hit.Name);
    }

    [Theory]
    [InlineData("create_task")]
    [InlineData("CREATE_TASK")]
    [InlineData("  create_task  ")]
    public void An_exact_tool_name_returns_only_that_tool(string query)
    {
        var hit = Assert.Single(Strategy.Search(query, TaskBoard, maxResults: 5));

        Assert.Equal("create_task", hit.Name);
    }

    [Theory]
    [InlineData("weather forecast for tomorrow")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("the a of")] // only stopwords
    public void Unrelated_or_empty_queries_match_nothing(string query) =>
        Assert.Empty(Strategy.Search(query, TaskBoard, maxResults: 5));

    // ---- ambiguity: how many results come back depends on how sure the evidence is ----

    [Fact]
    public void A_vague_query_still_surfaces_the_alternative()
    {
        // No literal phrase here, just loose word overlap: the engine can't tell "list my created
        // tasks" from "create a task", so list_tasks must come back as a candidate, not be cut.
        var names = Names(Strategy.Search("show my created tasks", TaskBoard, 5));

        Assert.Contains("list_tasks", names);
        Assert.Contains("create_task", names);
    }

    [Fact]
    public void A_single_keyword_match_also_returns_runners_up()
    {
        var names = Names(Strategy.Search("delegate a job", TaskBoard, 5));

        Assert.Equal("create_task", names[0]);
        Assert.True(names.Count > 1, "a keyword-only match is ambiguous, so alternatives should be included");
    }

    [Fact]
    public void A_keyword_phrase_match_keeps_results_tight()
    {
        // "assign work" is a multi-word keyword of create_task: decisive, so no padding with weak matches.
        var hit = Assert.Single(Strategy.Search("assign work to another agent", TaskBoard, 5));

        Assert.Equal("create_task", hit.Name);
    }

    private static readonly List<ToolSearchDocument> InvoiceDocs =
    [
        new("alpha_tool", "Handles invoices and receipts", []),
        new("beta_tool", "Handles receipts only", []),
    ];

    [Fact]
    public void Without_decisive_evidence_the_lower_threshold_applies()
    {
        // Thresholds pushed to the extremes so the two modes are unmistakable.
        var strategy = new LexicalToolSearchStrategy { RelativeThreshold = 0.99, AmbiguousRelativeThreshold = 0 };

        Assert.Equal(new[] { "alpha_tool", "beta_tool" }, Names(strategy.Search("invoices receipts", InvoiceDocs, 5)));
    }

    [Fact]
    public void With_decisive_evidence_the_higher_threshold_applies()
    {
        var strategy = new LexicalToolSearchStrategy { RelativeThreshold = 0.99, AmbiguousRelativeThreshold = 0 };
        var documents = new List<ToolSearchDocument>
        {
            new("alpha_tool", "Handles invoices and receipts", [], ["invoices receipts"]),
            new("beta_tool", "Handles receipts only", []),
        };

        var hit = Assert.Single(strategy.Search("invoices receipts", documents, 5));

        Assert.Equal("alpha_tool", hit.Name);
    }

    [Fact]
    public void The_relative_threshold_can_be_turned_off()
    {
        var strict = new LexicalToolSearchStrategy().Search("assign work to another agent", TaskBoard, 5);
        var loose = new LexicalToolSearchStrategy { RelativeThreshold = 0 }.Search("assign work to another agent", TaskBoard, 5);

        Assert.Single(strict);
        Assert.True(loose.Count > strict.Count, "with no threshold, weaker matches should come back too");
        Assert.Equal(strict[0].Name, loose[0].Name);
    }

    // ---- literal phrases ----

    [Fact]
    public void Literal_phrases_ignore_case_and_punctuation()
    {
        var documents = new List<ToolSearchDocument> { new("lister", "Zzz", [], ["Tasks I created"]) };

        Assert.Equal("lister", Assert.Single(Strategy.Search("TASKS, I created!", documents, 5)).Name);
    }

    [Fact]
    public void Literal_phrases_keep_word_order_and_tense()
    {
        var documents = new List<ToolSearchDocument> { new("lister", "Zzz", [], ["tasks i created"]) };

        Assert.Empty(Strategy.Search("create a task", documents, 5));
        Assert.Empty(Strategy.Search("created tasks i", documents, 5));
    }

    [Fact]
    public void Literal_phrases_match_whole_words_only()
    {
        var documents = new List<ToolSearchDocument> { new("mine", "Zzz", [], ["my tasks"]) };

        Assert.Empty(Strategy.Search("enemy tasks", documents, 5));
        Assert.Single(Strategy.Search("show my tasks", documents, 5));
    }

    // ---- everything else ----

    [Fact]
    public void Respects_max_results()
    {
        var documents = Enumerable.Range(1, 6)
            .Select(i => new ToolSearchDocument($"tool_{i}", "Converts spreadsheet files", []))
            .ToList();

        Assert.Equal(2, Strategy.Search("spreadsheet", documents, maxResults: 2).Count);
    }

    [Fact]
    public void A_multi_word_keyword_only_counts_as_a_whole_phrase()
    {
        var documents = new List<ToolSearchDocument>
        {
            new("alpha", "Does alpha things", ["hand off"]),
            new("beta", "Does beta things", []),
        };

        Assert.Empty(Strategy.Search("hand", documents, 5)); // "hand" alone is not the phrase
        Assert.Equal("alpha", Assert.Single(Strategy.Search("please hand off the work", documents, 5)).Name);
    }

    [Fact]
    public void Rare_words_outweigh_words_every_tool_shares()
    {
        // "report" is in every description; "spreadsheet" only in one. Searching for both should favour the one.
        var documents = new List<ToolSearchDocument>
        {
            new("export_csv", "Builds a report as csv", []),
            new("export_xlsx", "Builds a report as a spreadsheet", []),
            new("export_pdf", "Builds a report as pdf", []),
        };

        var hits = Strategy.Search("report spreadsheet", documents, 5);

        Assert.Equal("export_xlsx", hits[0].Name);
    }

    [Fact]
    public async Task SearchAsync_gives_the_same_answer_as_Search()
    {
        var sync = Strategy.Search("delegate a job", TaskBoard, 5);
        var async = await ((IToolSearchStrategy)Strategy).SearchAsync("delegate a job", TaskBoard, 5);

        Assert.Equal(sync, async);
    }
}
