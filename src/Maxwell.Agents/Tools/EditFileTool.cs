using System.ComponentModel;
using System.Text.RegularExpressions;

namespace Maxwell.Agents.Tools;

/// <summary>
/// "edit" - a surgical find-and-replace: <paramref name="oldText"/> must match
/// the file's content exactly (including whitespace) and must occur exactly
/// once, so the model is forced to include enough surrounding context to
/// uniquely identify the change it wants rather than guessing at line numbers.
///
/// Line endings are the one thing matched leniently: a line break in oldText (LF or CRLF)
/// matches either in the file. The model only ever sees lines through "read", which
/// hides the difference, so without this a multi-line edit of a Windows (CRLF) file could
/// never match, with an error that gave no hint why. And the edit changes nothing it wasn't
/// asked to: every other line keeps its own line ending, text inserted by newText uses the
/// line ending of the place it goes, and the file's encoding (BOM or not) is preserved.
/// </summary>
public sealed class EditFileTool(string rootDirectory)
{
    [Description(
        "Make a precise edit to an existing text file by replacing one exact, unique occurrence " +
        "of oldText with newText. oldText must match the file's actual content exactly (whitespace " +
        "included) and must appear exactly once - read the file first and include enough " +
        "surrounding context to make the match unique. Line endings (CRLF or LF) don't have to match. " +
        "Use an empty newText to delete oldText.")]
    public string Edit(
        [Description("Path to the file to edit, absolute or relative to the project's working directory.")]
        string path,
        [Description("The exact, unique text to find in the file.")]
        string oldText,
        [Description("The text to replace oldText with. Leave empty to delete oldText.")]
        string newText = "")
    {
        if (string.IsNullOrEmpty(oldText))
        {
            return "Error: oldText must not be empty.";
        }

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

        string original;
        TextFileFormat format;
        try
        {
            (original, format) = TextFileFormat.Read(fullPath);
        }
        catch (Exception ex)
        {
            return $"Error: could not read '{path}': {ex.Message}";
        }

        var matches = LineEndingTolerantPattern(oldText).Matches(original);
        switch (matches.Count)
        {
            case 0:
                return $"Error: oldText was not found in {ToolPaths.Display(rootDirectory, fullPath)}. " +
                       "Re-read the file and make sure oldText matches exactly, including whitespace.";
            case > 1:
                return $"Error: oldText occurs {matches.Count} times in {ToolPaths.Display(rootDirectory, fullPath)}; " +
                       "it must be unique. Include more surrounding context and try again.";
        }

        var match = matches[0];

        // Line breaks inside the new text follow the place they replace; if that has none, the file's dominant style.
        var newLine = match.Value.Contains("\r\n") ? "\r\n" : match.Value.Contains('\n') ? "\n" : format.NewLine;
        var replacement = TextFileFormat.WithNewLine(newText, newLine);

        var updated = string.Concat(original.AsSpan(0, match.Index), replacement, original.AsSpan(match.Index + match.Length));

        try
        {
            format.Write(fullPath, updated);
        }
        catch (Exception ex)
        {
            return $"Error: could not write '{path}': {ex.Message}";
        }

        return $"Edited {ToolPaths.Display(rootDirectory, fullPath)}: replaced 1 occurrence " +
               $"({oldText.Length} chars -> {newText.Length} chars)." +
               (format.UsesCrLf ? " Kept the file's CRLF line endings." : string.Empty);
    }

    /// <summary>
    /// Matches <paramref name="oldText"/> literally, except that each line break in it (LF or CRLF)
    /// matches either kind in the file.
    /// </summary>
    private static Regex LineEndingTolerantPattern(string oldText)
    {
        var lines = oldText.Replace("\r\n", "\n").Split('\n');
        return new Regex(string.Join(@"\r?\n", lines.Select(Regex.Escape)), RegexOptions.CultureInvariant);
    }
}
