using System.Text;

namespace Maxwell.Agents.Tools.Search;

/// <summary>
/// Keyword search over tool names, declared hints, and descriptions. No model, no network:
/// deterministic and instant, so it can be tested with a table of "query -> expected tool".
///
/// How a query is scored:
///  1. The query and every document are split into words (so "create_task" is "create" + "task"),
///     lowercased, stripped of stopwords, and reduced to Porter stems - "delegating" and
///     "delegates" both become "deleg", so inflections match.
///  2. Each query word is weighted by how *distinctive* it is across the catalog (IDF): a word
///     that appears in every tool's text ("task", on a task board) tells you little; a word that
///     appears in one tool's text tells you a lot.
///  3. A document scores, for every query word it contains, that word's weight times where it
///     was found: in the name (4), in a single-word keyword (3), in the description (1) - summed
///     when it appears in several places.
///  4. Decisive evidence adds more and changes how many results come back (below): a query that is
///     exactly a tool's name; a multi-word keyword whose words are all in the query ("hand off");
///     or a literal phrase the query contains as written ("tasks i created"). Literal phrases are
///     matched *without* stemming or stopword removal and with word order kept, which preserves
///     what the stemmer erases - "tasks i created" (list them) vs. "create a task" (make one).
///
/// How many results come back depends on how sure the evidence is:
///  - If the best hit has decisive evidence, only hits scoring at least <see cref="RelativeThreshold"/>
///    of it are kept, so one clear match isn't padded with weak ones.
///  - If the best hit rests only on loose word overlap, the query is treated as ambiguous and the
///    bar drops to <see cref="AmbiguousRelativeThreshold"/>, so plausible runners-up come back too.
///    The agent reads each description and chooses; a wrong top-1 is cheap when the right tool is
///    also in the list, and fatal when it was cut.
///
/// Limits: it matches words, not meaning. A synonym nobody listed as a keyword and that isn't in
/// the description won't match - that's what hints are for, and what an embedding-based
/// <see cref="IToolSearchStrategy"/> would address. No typo tolerance. English only: the stemmer
/// and stopwords are English, and so are tool descriptions, so agents are told to write their
/// queries in English.
/// </summary>
public sealed class LexicalToolSearchStrategy : IToolSearchStrategy
{
    private const double NameWeight = 4;
    private const double KeywordWeight = 3;
    private const double DescriptionWeight = 1;
    private const double KeywordPhraseBonus = 2;
    private const double LiteralPhraseBonus = 6;
    private const double ExactNameScore = 1000;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "to", "for", "of", "and", "or", "in", "on", "with", "at", "by", "as", "from",
        "i", "me", "my", "you", "your", "we", "it", "its", "this", "that", "these", "those",
        "is", "are", "was", "be", "can", "do", "does", "how", "some", "any", "all", "another", "other",
        "need", "want", "use", "using", "tool", "tools", "please", "help",
    };

    /// <summary>
    /// Fraction of the best hit's score a hit needs to be returned when the best hit has decisive
    /// evidence. 0 returns everything that matched at all; 1 returns only ties for the best.
    /// </summary>
    public double RelativeThreshold { get; init; } = 0.4;

    /// <summary>
    /// The same fraction when the best hit has only loose word-overlap evidence, i.e. the query is
    /// ambiguous. Lower than <see cref="RelativeThreshold"/> so alternatives survive.
    /// </summary>
    public double AmbiguousRelativeThreshold { get; init; } = 0.25;

    public Task<IReadOnlyList<ToolSearchHit>> SearchAsync(
        string query,
        IReadOnlyList<ToolSearchDocument> documents,
        int maxResults,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Search(query, documents, maxResults));

    public IReadOnlyList<ToolSearchHit> Search(string query, IReadOnlyList<ToolSearchDocument> documents, int maxResults)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0 || documents.Count == 0 || maxResults < 1)
        {
            return [];
        }

        var exact = documents.FirstOrDefault(d => string.Equals(d.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return [new ToolSearchHit(exact.Name, ExactNameScore)];
        }

        var queryStems = Stems(trimmed).Distinct().ToList();
        var paddedQuery = Pad(Words(trimmed));
        if (queryStems.Count == 0 && paddedQuery.Length <= 2)
        {
            return [];
        }

        var indexed = documents.Select(IndexedDocument.From).ToList();

        // IDF: log(1 + (N - df + 0.5) / (df + 0.5)), the BM25 form (always >= 0). df = how many
        // documents contain the stem anywhere.
        var documentCount = indexed.Count;
        var weight = queryStems.ToDictionary(
            stem => stem,
            stem =>
            {
                var df = indexed.Count(d => d.Contains(stem));
                return Math.Log(1 + (documentCount - df + 0.5) / (df + 0.5));
            });

        var scored = new List<(string Name, double Score, bool Decisive)>();
        foreach (var doc in indexed)
        {
            var score = 0.0;
            var decisive = false;

            foreach (var stem in queryStems)
            {
                var fieldWeight =
                    (doc.NameStems.Contains(stem) ? NameWeight : 0)
                    + (doc.KeywordStems.Contains(stem) ? KeywordWeight : 0)
                    + (doc.DescriptionStems.Contains(stem) ? DescriptionWeight : 0);
                score += weight[stem] * fieldWeight;
            }

            foreach (var phrase in doc.KeywordPhrases)
            {
                if (phrase.All(queryStems.Contains))
                {
                    score += KeywordPhraseBonus * phrase.Average(stem => weight[stem]);
                    decisive = true;
                }
            }

            // One literal phrase is as good as several; don't let a long list of near-duplicate phrases inflate the score.
            if (doc.LiteralPhrases.Any(paddedQuery.Contains))
            {
                score += LiteralPhraseBonus;
                decisive = true;
            }

            if (score > 0)
            {
                scored.Add((doc.Name, score, decisive));
            }
        }

        if (scored.Count == 0)
        {
            return [];
        }

        var best = scored.MaxBy(h => h.Score);
        var threshold = best.Decisive ? RelativeThreshold : AmbiguousRelativeThreshold;

        return scored
            .Where(h => h.Score >= best.Score * threshold)
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(h => new ToolSearchHit(h.Name, h.Score))
            .ToList();
    }

    private sealed record IndexedDocument(
        string Name,
        HashSet<string> NameStems,
        HashSet<string> KeywordStems,
        List<List<string>> KeywordPhrases,
        List<string> LiteralPhrases,
        HashSet<string> DescriptionStems)
    {
        public bool Contains(string stem) =>
            NameStems.Contains(stem) || KeywordStems.Contains(stem) || DescriptionStems.Contains(stem);

        public static IndexedDocument From(ToolSearchDocument document)
        {
            var keywords = document.Keywords
                .Select(k => Stems(k).ToList())
                .Where(k => k.Count > 0)
                .ToList();

            var literalPhrases = (document.Phrases ?? [])
                .Select(p => Words(p).ToList())
                .Where(words => words.Count > 0)
                .Select(Pad)
                .Distinct()
                .ToList();

            return new IndexedDocument(
                document.Name,
                Stems(document.Name).ToHashSet(),
                // Single-word keywords match on their own. The words of a multi-word keyword do NOT
                // count individually: "start working" must not make a bare "work" query match.
                keywords.Where(k => k.Count == 1).Select(k => k[0]).ToHashSet(),
                keywords.Where(k => k.Count > 1).ToList(),
                literalPhrases,
                Stems(document.Description).ToHashSet());
        }
    }

    // " tasks i created " - padded with spaces so a phrase only matches on whole-word boundaries.
    private static string Pad(IEnumerable<string> words) => " " + string.Join(' ', words) + " ";

    private static IEnumerable<string> Stems(string text) =>
        Words(text).Where(w => !StopWords.Contains(w)).Select(PorterStemmer.Stem);

    // Splits on anything that isn't a letter or digit, so "create_task" -> create, task and
    // "assigned_to_me" -> assigned, to, me.
    private static IEnumerable<string> Words(string text)
    {
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
