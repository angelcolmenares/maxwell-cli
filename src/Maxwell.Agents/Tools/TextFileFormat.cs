using System.Text;

namespace Maxwell.Agents.Tools;

/// <summary>
/// What a text file looks like on disk apart from its characters: its line endings and its
/// encoding (including whether it starts with a BOM). <see cref="EditFileTool"/> and
/// <see cref="WriteFileTool"/> read this before changing a file and write it back the same way,
/// so a one-line change doesn't rewrite every line ending of a Windows (CRLF) file, or add or
/// drop a BOM, which would show up as a whole-file change in a diff.
/// </summary>
/// <param name="NewLine">The file's dominant line ending, "\r\n" or "\n".</param>
/// <param name="Encoding">The encoding to write with; emits a BOM only if the file had one.</param>
/// <param name="NewLineKnown">False when the file has no line breaks, so <paramref name="NewLine"/> is just a default, not something to copy.</param>
internal sealed record TextFileFormat(string NewLine, Encoding Encoding, bool NewLineKnown = false)
{
    /// <summary>A file with no line breaks of its own to copy (new or single-line): LF, UTF-8 without BOM.</summary>
    public static TextFileFormat Default { get; } = new("\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    public bool UsesCrLf => NewLine == "\r\n";

    /// <summary>Reads a file's text and its format. Undecodable bytes become U+FFFD, as File.ReadAllText always has.</summary>
    public static (string Text, TextFileFormat Format) Read(string path) => Analyze(File.ReadAllBytes(path));

    public static (string Text, TextFileFormat Format) Analyze(byte[] bytes)
    {
        var (encoding, preambleLength) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);

        // The dominant line ending wins. Ties (including "no line breaks at all") get LF.
        var crlf = CountOf(text, "\r\n");
        var lf = CountOf(text, "\n") - crlf;
        return (text, new TextFileFormat(crlf > lf ? "\r\n" : "\n", encoding, NewLineKnown: crlf + lf > 0));
    }

    /// <summary>Writes <paramref name="text"/> using this format's encoding, including its BOM if it had one.</summary>
    public void Write(string path, string text) => File.WriteAllText(path, text, Encoding);

    /// <summary>Converts every line break in <paramref name="text"/> (CRLF or LF) to <paramref name="newLine"/>.</summary>
    public static string WithNewLine(string text, string newLine)
    {
        var lf = text.Replace("\r\n", "\n");
        return newLine == "\n" ? lf : lf.Replace("\n", newLine);
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 3);
        }

        // UTF-32 LE also starts FF FE; it's rare enough that it is left to the UTF-8 path below rather than half-supported.
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE && !(bytes.Length >= 4 && bytes[2] == 0 && bytes[3] == 0))
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), 2);
        }

        return (Default.Encoding, 0);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
