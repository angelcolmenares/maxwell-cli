using System.Text;
using Maxwell.Agents.Tools;

namespace Maxwell.Agents.Tests;

/// <summary>
/// edit and write must not change bytes they weren't asked to change: line endings of other
/// lines, and the BOM. Files here are created and checked at the byte level, because the whole
/// point is differences that are invisible when printed.
/// </summary>
public sealed class FileToolsLineEndingTests : IDisposable
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "maxwell-eol-" + Guid.NewGuid().ToString("N"));
    private readonly EditFileTool _edit;
    private readonly WriteFileTool _write;

    public FileToolsLineEndingTests()
    {
        Directory.CreateDirectory(_root);
        _edit = new EditFileTool(_root);
        _write = new WriteFileTool(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string PathOf(string name) => Path.Combine(_root, name);

    private string Create(string name, string text, Encoding? encoding = null)
    {
        File.WriteAllText(PathOf(name), text, encoding ?? new UTF8Encoding(false));
        return name;
    }

    private string Text(string name) => new UTF8Encoding(false).GetString(File.ReadAllBytes(PathOf(name)));

    // ---- edit: matching ----

    [Fact]
    public void A_multi_line_edit_matches_a_CRLF_file_and_keeps_CRLF()
    {
        Create("crlf.txt", "alpha\r\nbeta\r\ngamma\r\n");

        var result = _edit.Edit("crlf.txt", "alpha\nbeta", "ALPHA\nBETA");

        Assert.StartsWith("Edited", result);
        Assert.Equal("ALPHA\r\nBETA\r\ngamma\r\n", Text("crlf.txt"));
    }

    [Fact]
    public void The_result_mentions_that_CRLF_was_kept()
    {
        Create("crlf.txt", "a\r\nb\r\n");

        Assert.Contains("CRLF", _edit.Edit("crlf.txt", "a\nb", "x\ny"));
    }

    [Fact]
    public void An_oldText_written_with_CRLF_matches_an_LF_file_and_keeps_LF()
    {
        Create("lf.txt", "alpha\nbeta\ngamma\n");

        var result = _edit.Edit("lf.txt", "alpha\r\nbeta", "ALPHA\r\nBETA");

        Assert.StartsWith("Edited", result);
        Assert.Equal("ALPHA\nBETA\ngamma\n", Text("lf.txt"));
    }

    [Fact]
    public void A_single_line_edit_works_on_a_CRLF_file_and_touches_nothing_else()
    {
        Create("crlf.txt", "alpha\r\nbeta\r\ngamma\r\n");

        _edit.Edit("crlf.txt", "beta", "BETA");

        Assert.Equal("alpha\r\nBETA\r\ngamma\r\n", Text("crlf.txt"));
    }

    [Fact]
    public void Lines_outside_the_edit_keep_their_own_line_endings_in_a_mixed_file()
    {
        Create("mixed.txt", "one\r\ntwo\nthree\r\nfour\n");

        _edit.Edit("mixed.txt", "three", "THREE");

        Assert.Equal("one\r\ntwo\nTHREE\r\nfour\n", Text("mixed.txt"));
    }

    [Fact]
    public void New_line_breaks_follow_the_line_ending_of_the_place_they_go()
    {
        Create("crlf.txt", "alpha\r\nbeta\r\ngamma\r\n");
        Create("lf.txt", "alpha\nbeta\ngamma\n");

        _edit.Edit("crlf.txt", "beta", "beta\nbeta2");
        _edit.Edit("lf.txt", "beta", "beta\r\nbeta2");

        Assert.Equal("alpha\r\nbeta\r\nbeta2\r\ngamma\r\n", Text("crlf.txt"));
        Assert.Equal("alpha\nbeta\nbeta2\ngamma\n", Text("lf.txt"));
    }

    [Fact]
    public void The_match_must_still_be_unique_across_line_ending_styles()
    {
        Create("dup.txt", "x\r\ny\r\n--\r\nx\ny\n");

        var result = _edit.Edit("dup.txt", "x\ny", "z");

        Assert.Contains("occurs 2 times", result);
        Assert.Equal("x\r\ny\r\n--\r\nx\ny\n", Text("dup.txt")); // nothing written
    }

    [Fact]
    public void Everything_except_line_endings_is_still_matched_exactly()
    {
        Create("crlf.txt", "alpha\r\n  beta\r\n");

        var result = _edit.Edit("crlf.txt", "alpha\nbeta", "x"); // missing the two leading spaces

        Assert.Contains("not found", result);
        Assert.Equal("alpha\r\n  beta\r\n", Text("crlf.txt"));
    }

    [Fact]
    public void Regex_special_characters_in_oldText_are_matched_literally()
    {
        Create("code.txt", "if (a.b[0] == \"x+y\") {\r\n  run();\r\n}\r\n");

        var result = _edit.Edit("code.txt", "if (a.b[0] == \"x+y\") {\n  run();", "if (ok) {\n  go();");

        Assert.StartsWith("Edited", result);
        Assert.Equal("if (ok) {\r\n  go();\r\n}\r\n", Text("code.txt"));
    }

    [Fact]
    public void Deleting_text_with_an_empty_newText_still_works()
    {
        Create("crlf.txt", "alpha\r\nbeta\r\ngamma\r\n");

        _edit.Edit("crlf.txt", "beta\n", "");

        Assert.Equal("alpha\r\ngamma\r\n", Text("crlf.txt"));
    }

    [Fact]
    public void Errors_for_a_missing_file_or_empty_oldText_are_unchanged()
    {
        Assert.Contains("file not found", _edit.Edit("nope.txt", "a", "b"));
        Create("a.txt", "a");
        Assert.Contains("must not be empty", _edit.Edit("a.txt", "", "b"));
    }

    // ---- edit: BOM / encoding ----

    [Fact]
    public void Editing_keeps_a_UTF8_BOM()
    {
        Create("bom.txt", "alpha\r\nbeta\r\n", new UTF8Encoding(true));

        _edit.Edit("bom.txt", "beta", "BETA");

        var bytes = File.ReadAllBytes(PathOf("bom.txt"));
        Assert.Equal(Utf8Bom, bytes.Take(3).ToArray());
        Assert.Equal("alpha\r\nBETA\r\n", new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public void Editing_does_not_add_a_BOM_to_a_file_without_one()
    {
        Create("nobom.txt", "alpha\nbeta\n");

        _edit.Edit("nobom.txt", "beta", "BETA");

        Assert.NotEqual(Utf8Bom, File.ReadAllBytes(PathOf("nobom.txt")).Take(3).ToArray());
    }

    [Fact]
    public void Editing_keeps_a_UTF16_file_UTF16()
    {
        Create("utf16.txt", "alpha\r\nbeta\r\n", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        var result = _edit.Edit("utf16.txt", "alpha\nbeta", "ALPHA\nBETA");

        Assert.StartsWith("Edited", result);
        var bytes = File.ReadAllBytes(PathOf("utf16.txt"));
        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes.Take(2).ToArray());
        Assert.Equal("ALPHA\r\nBETA\r\n", Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
    }

    [Fact]
    public void Accented_text_survives_an_edit()
    {
        Create("es.txt", "ñandú\r\n¿qué pasa?\r\n");

        _edit.Edit("es.txt", "¿qué pasa?", "¿cómo estás?");

        Assert.Equal("ñandú\r\n¿cómo estás?\r\n", Text("es.txt"));
    }

    // ---- write ----

    [Fact]
    public void Overwriting_a_CRLF_file_keeps_CRLF()
    {
        Create("crlf.txt", "old\r\ncontent\r\n");

        var result = _write.Write("crlf.txt", "new\nfile\nbody\n");

        Assert.Equal("new\r\nfile\r\nbody\r\n", Text("crlf.txt"));
        Assert.Contains("Kept the file's CRLF line endings", result);
    }

    [Fact]
    public void Overwriting_an_LF_file_keeps_LF_even_if_the_content_has_CRLF()
    {
        Create("lf.txt", "old\ncontent\n");

        var result = _write.Write("lf.txt", "new\r\nfile\r\n");

        Assert.Equal("new\nfile\n", Text("lf.txt"));
        Assert.Contains("LF line endings", result);
    }

    [Fact]
    public void A_new_file_is_written_exactly_as_given()
    {
        _write.Write("lf-new.txt", "a\nb\n");
        _write.Write("crlf-new.txt", "a\r\nb\r\n");

        Assert.Equal("a\nb\n", Text("lf-new.txt"));
        Assert.Equal("a\r\nb\r\n", Text("crlf-new.txt"));
        Assert.NotEqual(Utf8Bom, File.ReadAllBytes(PathOf("lf-new.txt")).Take(3).ToArray());
    }

    [Fact]
    public void Overwriting_a_file_with_no_line_breaks_does_not_guess_a_style()
    {
        Create("one-line.txt", "just one line");

        var result = _write.Write("one-line.txt", "a\r\nb\r\n");

        Assert.Equal("a\r\nb\r\n", Text("one-line.txt"));
        Assert.True(!result.Contains("Kept"), "there was no line ending to keep");
    }

    [Fact]
    public void Overwriting_keeps_a_UTF8_BOM()
    {
        Create("bom.txt", "old\r\n", new UTF8Encoding(true));

        _write.Write("bom.txt", "new\n");

        var bytes = File.ReadAllBytes(PathOf("bom.txt"));
        Assert.Equal(Utf8Bom, bytes.Take(3).ToArray());
        Assert.Equal("new\r\n", new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public void Content_with_quotes_dollar_signs_backticks_and_accents_round_trips()
    {
        const string tricky = "echo \"$HOME\" `date` 'it''s' ñandú ¿qué?\nline2";

        _write.Write("tricky.txt", tricky);

        Assert.Equal(tricky, Text("tricky.txt"));
    }
}
