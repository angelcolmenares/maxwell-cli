using System.ComponentModel;
using System.Text;

namespace Maxwell.Agents.Tools;

/// <summary>
/// "read" - lets the model examine a file's contents before deciding how (or
/// whether) to edit it. Lines are numbered so the model can refer back to
/// specific locations, mirroring how most coding-agent "read" tools behave; the
/// numbering is display-only and is never mistaken for file content by
/// <see cref="EditFileTool"/>, which always matches against the raw file text.
/// </summary>
public sealed class ReadFileTool(string rootDirectory)
{
    private const int MaxLines = 2000;
    private const int HeadTailLines = 500;

    [Description(
        "Read a text file's contents so you can inspect it before editing. Returns the file " +
        "with 1-based line numbers prefixed (the prefix is for your reference only and is not " +
        "part of the file's actual content). Large files are truncated; pass startLine/endLine " +
        "to page through them.")]
    public string Read(
        [Description("Path to the file, absolute or relative to the project's working directory.")]
        string path,
        [Description("1-based line number to start at (inclusive). Omit to start at line 1.")]
        int? startLine = null,
        [Description("1-based line number to stop at (inclusive). Omit to read to the end of the file.")]
        int? endLine = null)
    {
        string fullPath;
        try
        {
            fullPath = ToolPaths.Resolve(rootDirectory, path);
        }
        catch (Exception ex)
        {
            return $"Error: invalid path '{path}' ({ex.Message}).";
        }

        if (!File.Exists(fullPath))
        {
            return $"Error: file not found: {ToolPaths.Display(rootDirectory, fullPath)}";
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(fullPath);
        }
        catch (Exception ex)
        {
            return $"Error: could not read '{path}': {ex.Message}";
        }

        if (lines.Length == 0)
        {
            return $"{ToolPaths.Display(rootDirectory, fullPath)} is empty.";
        }

        var from = Math.Max(1, startLine ?? 1);
        var to = Math.Min(lines.Length, endLine ?? lines.Length);
        if (from > to)
        {
            return $"Error: startLine ({from}) is after endLine ({to}); file has {lines.Length} lines.";
        }

        var explicitRange = startLine is not null || endLine is not null;
        var builder = new StringBuilder();
        builder.AppendLine($"{ToolPaths.Display(rootDirectory, fullPath)} ({lines.Length} lines):");

        if (!explicitRange && to - from + 1 > MaxLines)
        {
            AppendLines(builder, lines, from, from + HeadTailLines - 1);
            builder.AppendLine($"... [{to - from + 1 - 2 * HeadTailLines} lines omitted; pass startLine/endLine to page through the rest] ...");
            AppendLines(builder, lines, to - HeadTailLines + 1, to);
        }
        else
        {
            AppendLines(builder, lines, from, to);
        }

        return builder.ToString();
    }

    private static void AppendLines(StringBuilder builder, IReadOnlyList<string> lines, int from, int to)
    {
        for (var i = from; i <= to; i++)
        {
            builder.Append(i).Append('\t').AppendLine(lines[i - 1]);
        }
    }
}
