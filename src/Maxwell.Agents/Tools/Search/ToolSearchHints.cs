namespace Maxwell.Agents.Tools.Search;

/// <summary>
/// Extra search signals a tool can declare beyond its name and description. Both exist because
/// descriptions describe what a tool does, while people (and models) ask for it in other words.
/// </summary>
public sealed record ToolSearchHints
{
    /// <summary>
    /// Synonyms and related words ("delegate", "dispatch"), matched the way description text is:
    /// lowercased and stemmed, so "delegating" matches "delegate". A multi-word keyword ("hand off")
    /// counts only when the query contains all of its words.
    /// </summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>
    /// Literal phrases, matched as written: no stemming, no stopword removal, word order kept
    /// (case and punctuation are ignored). Use these where the exact wording carries the meaning
    /// that stemming would erase - "tasks i created" means *list my tasks*, while "create a task"
    /// means *make one*, and a stemmer sees those as the same words. A phrase that matches is
    /// strong, decisive evidence for the tool.
    /// </summary>
    public IReadOnlyList<string> Phrases { get; init; } = [];

    public static ToolSearchHints None { get; } = new();
}
