namespace Maxwell.Agents.Tools.Search;

/// <summary>What a search strategy sees of one catalog tool.</summary>
/// <param name="Name">The tool's name, e.g. "create_task".</param>
/// <param name="Description">The tool's description, as the model would see it.</param>
/// <param name="Keywords">
/// Extra search terms declared for the tool: synonyms and phrases a user or model might use that
/// the name and description don't contain ("delegate", "hand off"). May be empty.
/// </param>
public sealed record ToolSearchDocument(string Name, string Description, IReadOnlyList<string> Keywords);

/// <param name="Name">The matching tool's name.</param>
/// <param name="Score">Relevance; only meaningful for comparing hits from the same search.</param>
public sealed record ToolSearchHit(string Name, double Score);

/// <summary>
/// Decides which catalog tools match what an agent asked for in search_tools. The catalog hands
/// the strategy the full set of documents on every call, so a strategy stays stateless from the
/// caller's point of view (an embedding-based one can cache vectors internally, keyed by document
/// content).
///
/// Implementations return hits best-first, already trimmed to what's actually relevant -
/// returning a weak match just because it's the next-best costs the agent context and can send it
/// down the wrong path. An empty list means "nothing matched", and the catalog tool then shows the
/// agent the available tool names instead.
///
/// The default is <see cref="LexicalToolSearchStrategy"/>. Async so that strategies that need I/O
/// (e.g. an embedding server) fit the same contract.
/// </summary>
public interface IToolSearchStrategy
{
    Task<IReadOnlyList<ToolSearchHit>> SearchAsync(
        string query,
        IReadOnlyList<ToolSearchDocument> documents,
        int maxResults,
        CancellationToken cancellationToken = default);
}
