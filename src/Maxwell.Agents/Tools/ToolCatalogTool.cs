using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Maxwell.Agents.Tools.Search;
using Microsoft.Extensions.AI;

namespace Maxwell.Agents.Tools;

/// <summary>
/// "search_tools" / "call_tool" - lets an agent keep a short always-loaded tool
/// list (read/bash/edit/write) and still reach everything else: it searches a
/// catalog by describing what it needs, gets back each match's description and
/// parameters, then runs the tool through call_tool.
///
/// Why a call_tool proxy instead of adding discovered tools to the agent's live
/// tool list: the tool list is fixed when the agent is created, and mutating it
/// mid-run depends on framework internals. The proxy works with any provider and
/// any model; the trade-off is that a model must put the arguments in a nested
/// object rather than use native function calling for that tool. The search result
/// stays in the conversation history, so later turns can call the tool without
/// searching again.
///
/// Catalog tools are expected to already be wrapped by the hook pipeline (see
/// MaxwellSessionService), so hooks see the real tool name and arguments, not
/// just "call_tool".
/// </summary>
public sealed class ToolCatalogTool
{
    private const int MaxResultsCap = 10;

    private readonly IReadOnlyList<AIFunction> _catalog;
    private readonly IToolSearchStrategy _strategy;
    private readonly IReadOnlyList<ToolSearchDocument> _documents;

    /// <param name="catalog">The tools reachable through search_tools/call_tool.</param>
    /// <param name="strategy">Decides which catalog tools match a query. See <see cref="IToolSearchStrategy"/>.</param>
    /// <param name="keywords">Extra search terms per tool name (synonyms the name/description lack). Optional.</param>
    public ToolCatalogTool(
        IReadOnlyList<AIFunction> catalog,
        IToolSearchStrategy strategy,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? keywords = null)
    {
        _catalog = catalog;
        _strategy = strategy;
        _documents = catalog
            .Select(tool => new ToolSearchDocument(
                tool.Name,
                tool.Description,
                keywords is not null && keywords.TryGetValue(tool.Name, out var tags) ? tags : []))
            .ToList();
    }

    [Description(
        "Search the catalog of additional tools that are not loaded by default. Describe the capability " +
        "you need in a few words (e.g. \"assign work to another agent\"). Returns each matching tool's " +
        "description and parameters. Then run it with call_tool.")]
    public async Task<string> SearchTools(
        [Description("What you need to do, in a few plain words.")]
        string query,
        [Description("Maximum number of tools to return (default 5).")]
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        maxResults = Math.Clamp(maxResults, 1, MaxResultsCap);

        IReadOnlyList<ToolSearchHit> hits;
        try
        {
            hits = await _strategy.SearchAsync(query ?? string.Empty, _documents, maxResults, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A strategy that depends on something external (an embedding server, ...) being down
            // shouldn't leave the agent unable to find any tool: fall back to plain keyword search.
            hits = await new LexicalToolSearchStrategy().SearchAsync(query ?? string.Empty, _documents, maxResults, cancellationToken);
        }

        var matches = hits
            .Select(hit => _catalog.FirstOrDefault(t => string.Equals(t.Name, hit.Name, StringComparison.OrdinalIgnoreCase)))
            .OfType<AIFunction>()
            .ToList();

        if (matches.Count == 0)
        {
            return $"No tool matched '{query}'. Available tools: {string.Join(", ", _catalog.Select(t => t.Name).Order())}. " +
                   "Search again using different words, or one of these names.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Found {matches.Count} tool(s). Run one with call_tool(name, arguments).");
        foreach (var tool in matches)
        {
            builder.AppendLine();
            builder.AppendLine($"{tool.Name} - {tool.Description}");
            builder.AppendLine("  parameters:");
            builder.AppendLine(DescribeParameters(tool, indent: "    "));
            builder.AppendLine($"  example: {BuildExample(tool)}");
        }

        return builder.ToString();
    }

    [Description(
        "Run a tool found with search_tools. Pass its exact name and an arguments object whose keys are the " +
        "tool's parameter names. Use an empty object {} for a tool with no parameters.")]
    public async Task<string> CallTool(
        [Description("Exact tool name, as returned by search_tools.")]
        string name,
        [Description("Object of parameter values, e.g. {\"assignedAgent\": \"Maxwell\", \"description\": \"...\"}.")]
        Dictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var tool = _catalog.FirstOrDefault(t => string.Equals(t.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (tool is null)
        {
            return $"Error: no catalog tool named '{name}'. Call search_tools to find the right name.";
        }

        // Parameterless ctor + indexer rather than a dictionary-copying constructor: values stay as
        // the JsonElements the model produced, which AIFunction invocation knows how to bind.
        var functionArguments = new AIFunctionArguments();
        if (arguments is not null)
        {
            foreach (var (key, value) in arguments)
            {
                functionArguments[key] = value;
            }
        }

        try
        {
            var result = await tool.InvokeAsync(functionArguments, cancellationToken);
            return result switch
            {
                null => "(no output)",
                string text => text,
                _ => JsonSerializer.Serialize(result),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error calling '{tool.Name}': {ex.Message}\nExpected parameters:\n{DescribeParameters(tool, indent: "  ")}";
        }
    }

    /// <summary>
    /// A short comma-separated list of catalog tool names, for the "tools available on demand"
    /// hint in an agent's instructions. Names only: schemas stay out of context until searched.
    /// </summary>
    public static string NameHint(IReadOnlyList<AIFunction> catalog, int maxNames = 20)
    {
        var names = catalog.Select(t => t.Name).Order().ToList();
        var shown = string.Join(", ", names.Take(maxNames));
        return names.Count > maxNames ? $"{shown}, ... (+{names.Count - maxNames} more)" : shown;
    }

    // ---- describing how to call a tool ----

    private static string DescribeParameters(AIFunction tool, string indent)
    {
        var schema = tool.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object
            || !properties.EnumerateObject().Any())
        {
            return $"{indent}(none)";
        }

        var required = RequiredNames(schema);
        var builder = new StringBuilder();
        foreach (var property in properties.EnumerateObject())
        {
            var (type, description) = ReadProperty(property.Value);
            var requirement = required.Contains(property.Name) ? "required" : "optional";
            builder.Append($"{indent}- {property.Name} ({type}, {requirement})");
            if (!string.IsNullOrWhiteSpace(description))
            {
                builder.Append($": {description}");
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildExample(AIFunction tool)
    {
        var parts = new List<string>();
        var schema = tool.JsonSchema;

        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            var required = RequiredNames(schema);
            foreach (var property in properties.EnumerateObject().Where(p => required.Contains(p.Name)))
            {
                var (type, _) = ReadProperty(property.Value);
                var placeholder = type switch
                {
                    "integer" or "number" => "0",
                    "boolean" => "false",
                    "array" => "[]",
                    "object" => "{}",
                    _ => "\"...\"",
                };
                parts.Add($"\"{property.Name}\": {placeholder}");
            }
        }

        return $"call_tool(name=\"{tool.Name}\", arguments={{{string.Join(", ", parts)}}})";
    }

    private static HashSet<string> RequiredNames(JsonElement schema)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                if (item.GetString() is { } name)
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    private static (string Type, string? Description) ReadProperty(JsonElement property)
    {
        // A property schema can legally be just `true` (any value), so don't assume it's an object.
        if (property.ValueKind != JsonValueKind.Object)
        {
            return ("any", null);
        }

        var type = "any";
        if (property.TryGetProperty("type", out var typeElement))
        {
            type = typeElement.ValueKind == JsonValueKind.Array
                ? string.Join("|", typeElement.EnumerateArray().Select(e => e.ToString()).Where(t => t != "null"))
                : typeElement.ToString();
        }

        string? description = property.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;

        return (type, description);
    }
}
