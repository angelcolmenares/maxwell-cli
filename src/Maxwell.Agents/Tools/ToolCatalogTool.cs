using System.ComponentModel;
using System.Text;
using System.Text.Json;
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
public sealed class ToolCatalogTool(IReadOnlyList<AIFunction> catalog)
{
    private const int MaxResultsCap = 10;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "to", "for", "of", "and", "or", "in", "on", "with", "i", "me", "my",
        "need", "want", "use", "tool", "tools", "that", "can", "how", "do", "is", "it",
    };

    [Description(
        "Search the catalog of additional tools that are not loaded by default. Describe the capability " +
        "you need in a few words (e.g. \"assign work to another agent\"). Returns each matching tool's " +
        "description and parameters. Then run it with call_tool.")]
    public string SearchTools(
        [Description("What you need to do, in a few plain words.")]
        string query,
        [Description("Maximum number of tools to return (default 5).")]
        int maxResults = 5)
    {
        maxResults = Math.Clamp(maxResults, 1, MaxResultsCap);

        var matches = Rank(query ?? string.Empty, maxResults);
        if (matches.Count == 0)
        {
            return $"No tool matched '{query}'. Available tools: {string.Join(", ", catalog.Select(t => t.Name).Order())}. " +
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
        var tool = catalog.FirstOrDefault(t => string.Equals(t.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
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

    // ---- search ----

    private List<AIFunction> Rank(string query, int max)
    {
        var terms = Tokenize(query).Select(Stem).Distinct().ToList();
        var exact = query.Trim();

        return catalog
            .Select(tool => (tool, score: Score(tool, exact, terms)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.tool.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => x.tool)
            .ToList();
    }

    private static int Score(AIFunction tool, string exactQuery, List<string> terms)
    {
        if (string.Equals(tool.Name, exactQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        var nameStems = Tokenize(tool.Name).Select(Stem).ToHashSet();
        var descriptionStems = Tokenize(tool.Description).Select(Stem).ToHashSet();

        var score = 0;
        foreach (var term in terms)
        {
            if (nameStems.Contains(term)) score += 4;
            if (descriptionStems.Contains(term)) score += 2;
        }

        return score;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
            {
                var token = current.ToString().ToLowerInvariant();
                if (!StopWords.Contains(token))
                {
                    tokens.Add(token);
                }

                current.Clear();
            }
        }

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else
            {
                Flush(); // '_', '-', spaces, punctuation all separate words, so create_task -> create, task
            }
        }

        Flush();
        return tokens;
    }

    // Deliberately crude: strip one common suffix so "delegating"/"delegate"/"delegates" all meet at "delegat".
    private static string Stem(string word)
    {
        foreach (var suffix in new[] { "ing", "ed", "es", "s", "e" })
        {
            if (word.Length - suffix.Length >= 3 && word.EndsWith(suffix, StringComparison.Ordinal))
            {
                return word[..^suffix.Length];
            }
        }

        return word;
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
