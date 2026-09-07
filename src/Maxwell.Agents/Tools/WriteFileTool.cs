using System.ComponentModel;

namespace Maxwell.Agents.Tools;

/// <summary>
/// "write" - creates a new file or completely overwrites an existing one. For
/// small, targeted changes to existing files prefer <see cref="EditFileTool"/>;
/// this tool is for brand-new files or full rewrites.
/// </summary>
public sealed class WriteFileTool(string rootDirectory)
{
    [Description(
        "Create a new file or completely overwrite an existing one with the given content. " +
        "Parent directories are created automatically. For small changes to an existing file, " +
        "prefer the 'edit' tool instead of rewriting the whole file.")]
    public string Write(
        [Description("Path to the file to create or overwrite, absolute or relative to the project's working directory.")]
        string path,
        [Description("The full content to write to the file, replacing anything already there.")]
        string content)
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

        var existed = File.Exists(fullPath);

        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, content);
        }
        catch (Exception ex)
        {
            return $"Error: could not write '{path}': {ex.Message}";
        }

        var lineCount = content.Length == 0 ? 0 : content.Split('\n').Length;
        var verb = existed ? "Overwrote" : "Created";
        return $"{verb} {ToolPaths.Display(rootDirectory, fullPath)} ({content.Length} chars, {lineCount} lines).";
    }
}
