namespace Maxwell.Agents.Tools.Search;

/// <summary>
/// The Porter (1980) stemming algorithm, as published ("An algorithm for suffix stripping"). It
/// maps inflected English words to a shared stem - "delegating", "delegates", "delegated" all
/// become "deleg" - so a search for one form finds the others. Stems aren't meant to be read as
/// words, only compared with each other.
///
/// This replaces an earlier one-suffix-strip heuristic that mishandled cases like
/// "process"/"processes". English only; other languages pass through mostly unchanged, which is
/// harmless (they just match on exact words).
///
/// Verified against NLTK's PorterStemmer in ORIGINAL_ALGORITHM mode over a large word list.
/// </summary>
internal static class PorterStemmer
{
    private delegate bool Condition(string stem);

    private readonly record struct Rule(string Suffix, string Replacement, Condition? When);

    private static readonly Rule[] Step1aRules =
    [
        new("sses", "ss", null),
        new("ies", "i", null),
        new("ss", "ss", null),
        new("s", "", null),
    ];

    private static readonly Rule[] Step2Rules =
    [
        new("ational", "ate", MeasureAbove0),
        new("tional", "tion", MeasureAbove0),
        new("enci", "ence", MeasureAbove0),
        new("anci", "ance", MeasureAbove0),
        new("izer", "ize", MeasureAbove0),
        new("abli", "able", MeasureAbove0),
        new("alli", "al", MeasureAbove0),
        new("entli", "ent", MeasureAbove0),
        new("eli", "e", MeasureAbove0),
        new("ousli", "ous", MeasureAbove0),
        new("ization", "ize", MeasureAbove0),
        new("ation", "ate", MeasureAbove0),
        new("ator", "ate", MeasureAbove0),
        new("alism", "al", MeasureAbove0),
        new("iveness", "ive", MeasureAbove0),
        new("fulness", "ful", MeasureAbove0),
        new("ousness", "ous", MeasureAbove0),
        new("aliti", "al", MeasureAbove0),
        new("iviti", "ive", MeasureAbove0),
        new("biliti", "ble", MeasureAbove0),
    ];

    private static readonly Rule[] Step3Rules =
    [
        new("icate", "ic", MeasureAbove0),
        new("ative", "", MeasureAbove0),
        new("alize", "al", MeasureAbove0),
        new("iciti", "ic", MeasureAbove0),
        new("ical", "ic", MeasureAbove0),
        new("ful", "", MeasureAbove0),
        new("ness", "", MeasureAbove0),
    ];

    private static readonly Rule[] Step4Rules =
    [
        new("al", "", MeasureAbove1),
        new("ance", "", MeasureAbove1),
        new("ence", "", MeasureAbove1),
        new("er", "", MeasureAbove1),
        new("ic", "", MeasureAbove1),
        new("able", "", MeasureAbove1),
        new("ible", "", MeasureAbove1),
        new("ant", "", MeasureAbove1),
        new("ement", "", MeasureAbove1),
        new("ment", "", MeasureAbove1),
        new("ent", "", MeasureAbove1),
        new("ion", "", stem => Measure(stem) > 1 && stem.Length > 0 && (stem[^1] == 's' || stem[^1] == 't')),
        new("ou", "", MeasureAbove1),
        new("ism", "", MeasureAbove1),
        new("ate", "", MeasureAbove1),
        new("iti", "", MeasureAbove1),
        new("ous", "", MeasureAbove1),
        new("ive", "", MeasureAbove1),
        new("ize", "", MeasureAbove1),
    ];

    /// <summary>Stems one word. Expects letters only; non-letters are the caller's problem (the tokenizer strips them).</summary>
    public static string Stem(string word)
    {
        word = word.ToLowerInvariant();
        if (word.Length <= 2)
        {
            return word;
        }

        word = Step1a(word);
        word = Step1b(word);
        word = Step1c(word);
        word = ApplyFirstMatch(word, Step2Rules);
        word = ApplyFirstMatch(word, Step3Rules);
        word = ApplyFirstMatch(word, Step4Rules);
        word = Step5a(word);
        word = Step5b(word);
        return word;
    }

    // ---- steps ----

    private static string Step1a(string word) => ApplyFirstMatch(word, Step1aRules);

    private static string Step1b(string word)
    {
        if (word.EndsWith("eed", StringComparison.Ordinal))
        {
            var eedStem = word[..^3];
            return Measure(eedStem) > 0 ? eedStem + "ee" : word;
        }

        string? intermediate = null;
        foreach (var suffix in new[] { "ed", "ing" })
        {
            if (word.EndsWith(suffix, StringComparison.Ordinal))
            {
                var candidate = word[..^suffix.Length];
                if (ContainsVowel(candidate))
                {
                    intermediate = candidate;
                    break;
                }
            }
        }

        if (intermediate is null)
        {
            return word;
        }

        // After removing -ed/-ing, tidy the result.
        if (intermediate.EndsWith("at", StringComparison.Ordinal)) return intermediate + "e";
        if (intermediate.EndsWith("bl", StringComparison.Ordinal)) return intermediate + "e";
        if (intermediate.EndsWith("iz", StringComparison.Ordinal)) return intermediate + "e";

        if (EndsWithDoubleConsonant(intermediate))
        {
            // "hopp" -> "hop", but "fall"/"hiss"/"fizz" keep their double letter.
            var last = intermediate[^1];
            return last is 'l' or 's' or 'z' ? intermediate : intermediate[..^1];
        }

        return Measure(intermediate) == 1 && EndsWithCvc(intermediate) ? intermediate + "e" : intermediate;
    }

    private static string Step1c(string word) =>
        word.EndsWith('y') && ContainsVowel(word[..^1]) ? word[..^1] + "i" : word;

    private static string Step5a(string word)
    {
        if (!word.EndsWith('e'))
        {
            return word;
        }

        var stem = word[..^1];
        if (Measure(stem) > 1)
        {
            return stem;
        }

        return Measure(stem) == 1 && !EndsWithCvc(stem) ? stem : word;
    }

    private static string Step5b(string word) =>
        word.EndsWith("ll", StringComparison.Ordinal) && Measure(word[..^1]) > 1 ? word[..^1] : word;

    // ---- rule application ----

    // The first rule (in list order) whose suffix matches is the only one considered: if its
    // condition fails, the word is left alone rather than trying shorter suffixes. List order puts
    // longer suffixes first wherever two could match ("ational" before "tional").
    private static string ApplyFirstMatch(string word, Rule[] rules)
    {
        foreach (var rule in rules)
        {
            if (!word.EndsWith(rule.Suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var stem = word[..^rule.Suffix.Length];
            return rule.When is null || rule.When(stem) ? stem + rule.Replacement : word;
        }

        return word;
    }

    private static bool MeasureAbove0(string stem) => Measure(stem) > 0;

    private static bool MeasureAbove1(string stem) => Measure(stem) > 1;

    // ---- word shape ----

    private static bool IsConsonant(string word, int i)
    {
        switch (word[i])
        {
            case 'a' or 'e' or 'i' or 'o' or 'u':
                return false;
            case 'y':
                // 'y' is a consonant at the start or after a vowel, and a vowel after a consonant.
                return i == 0 || !IsConsonant(word, i - 1);
            default:
                return true;
        }
    }

    private static bool ContainsVowel(string stem)
    {
        for (var i = 0; i < stem.Length; i++)
        {
            if (!IsConsonant(stem, i))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Porter's "measure" m: how many vowel-run-then-consonant-run pairs the stem contains.</summary>
    private static int Measure(string stem)
    {
        var count = 0;
        var previousWasVowel = false;
        for (var i = 0; i < stem.Length; i++)
        {
            var consonant = IsConsonant(stem, i);
            if (consonant && previousWasVowel)
            {
                count++;
            }

            previousWasVowel = !consonant;
        }

        return count;
    }

    private static bool EndsWithDoubleConsonant(string word) =>
        word.Length >= 2 && word[^1] == word[^2] && IsConsonant(word, word.Length - 1);

    // consonant-vowel-consonant at the end, where the final consonant isn't w, x or y ("*o" in the paper).
    private static bool EndsWithCvc(string word) =>
        word.Length >= 3
        && IsConsonant(word, word.Length - 3)
        && !IsConsonant(word, word.Length - 2)
        && IsConsonant(word, word.Length - 1)
        && word[^1] is not ('w' or 'x' or 'y');
}
