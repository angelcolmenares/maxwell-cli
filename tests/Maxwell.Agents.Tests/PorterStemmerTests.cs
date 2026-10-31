using Maxwell.Agents.Tools.Search;

namespace Maxwell.Agents.Tests;

public class PorterStemmerTests
{
    // Expected values come from NLTK's PorterStemmer in ORIGINAL_ALGORITHM mode (the algorithm as
    // published in 1980); the implementation was also checked against it over ~45,000 words.
    [Theory]
    [InlineData("caresses", "caress")]
    [InlineData("ponies", "poni")]
    [InlineData("ties", "ti")]
    [InlineData("cats", "cat")]
    [InlineData("feed", "feed")]
    [InlineData("agreed", "agre")]
    [InlineData("plastered", "plaster")]
    [InlineData("bled", "bled")]
    [InlineData("motoring", "motor")]
    [InlineData("sing", "sing")]
    [InlineData("conflated", "conflat")]
    [InlineData("troubled", "troubl")]
    [InlineData("sized", "size")]
    [InlineData("hopping", "hop")]
    [InlineData("tanned", "tan")]
    [InlineData("falling", "fall")]
    [InlineData("hissing", "hiss")]
    [InlineData("fizzed", "fizz")]
    [InlineData("failing", "fail")]
    [InlineData("filing", "file")]
    [InlineData("happy", "happi")]
    [InlineData("sky", "sky")]
    [InlineData("relational", "relat")]
    [InlineData("conditional", "condit")]
    [InlineData("rational", "ration")]
    [InlineData("digitizer", "digit")]
    [InlineData("operator", "oper")]
    [InlineData("feudalism", "feudal")]
    [InlineData("decisiveness", "decis")]
    [InlineData("hopefulness", "hope")]
    [InlineData("formaliti", "formal")]
    [InlineData("sensibiliti", "sensibl")]
    [InlineData("triplicate", "triplic")]
    [InlineData("electrical", "electr")]
    [InlineData("goodness", "good")]
    [InlineData("allowance", "allow")]
    [InlineData("adjustable", "adjust")]
    [InlineData("replacement", "replac")]
    [InlineData("adoption", "adopt")]
    [InlineData("communism", "commun")]
    [InlineData("effective", "effect")]
    [InlineData("probate", "probat")]
    [InlineData("rate", "rate")]
    [InlineData("cease", "ceas")]
    [InlineData("controll", "control")]
    [InlineData("roll", "roll")]
    public void Matches_the_published_algorithm(string word, string expected) =>
        Assert.Equal(expected, PorterStemmer.Stem(word));

    // The point of stemming: inflections of one word meet at the same stem.
    [Theory]
    [InlineData("delegate", "delegating")]
    [InlineData("delegate", "delegates")]
    [InlineData("delegate", "delegated")]
    [InlineData("process", "processes")]
    [InlineData("assign", "assigned")]
    [InlineData("task", "tasks")]
    [InlineData("create", "creating")]
    [InlineData("create", "created")]
    [InlineData("finish", "finished")]
    public void Inflections_share_a_stem(string first, string second) =>
        Assert.Equal(PorterStemmer.Stem(first), PorterStemmer.Stem(second));

    [Fact]
    public void Ignores_case() => Assert.Equal(PorterStemmer.Stem("running"), PorterStemmer.Stem("RUNNING"));

    [Theory]
    [InlineData("a")]
    [InlineData("to")]
    [InlineData("me")]
    public void Leaves_very_short_words_alone(string word) => Assert.Equal(word, PorterStemmer.Stem(word));
}
