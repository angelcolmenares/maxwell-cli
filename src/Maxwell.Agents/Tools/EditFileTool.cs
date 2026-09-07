using System.ComponentModel;

namespace Maxwell.Agents.Tools;

/// <summary>
/// "edit" - a surgical find-and-replace: <paramref name="oldText"/> must match
/// the file's raw content exactly (including whitespace) and must occur exactly
/// once, so the model is forced to include enough surrounding context to
/// uniquely identify the change it wants rather than guessing at line numbers.
/// </summary>
public sealed class EditFileTool(string rootDirectory)
{
    [Description(
        "Make a precise edit to an existing text file by replacing one exact, unique occurrence " +
        "of oldText with newText. oldText must match the file's actual content exactly (whitespace " +
        "included) and must appear exactly once - read the file first and include enough " +
        "surrounding context to make the match unique. Use an empty newText to delete oldText.")]
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
        try
        {
            original = File.ReadAllText(fullPath);
        }
        catch (Exception ex)
        {
            return $"Error: could not read '{path}': {ex.Message}";
        }

        var occurrences = CountOccurrences(original, oldText);
        switch (occurrences)
        {
            case 0:
                return $"Error: oldText was not found in {ToolPaths.Display(rootDirectory, fullPath)}. " +
                       "Re-read the file and make sure oldText matches exactly, including whitespace.";
            case > 1:
                return $"Error: oldText occurs {occurrences} times in {ToolPaths.Display(rootDirectory, fullPath)}; " +
                       "it must be unique. Include more surrounding context and try again.";
        }

        var index = original.IndexOf(oldText, StringComparison.Ordinal);
        var updated = string.Concat(original.AsSpan(0, index), newText, original.AsSpan(index + oldText.Length));

        try
        {
            File.WriteAllText(fullPath, updated);
        }
        catch (Exception ex)
        {
            return $"Error: could not write '{path}': {ex.Message}";
        }

        return $"Edited {ToolPaths.Display(rootDirectory, fullPath)}: replaced 1 occurrence " +
               $"({oldText.Length} chars -> {newText.Length} chars).";
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
