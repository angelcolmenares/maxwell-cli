using System.ComponentModel;

namespace Maxwell.Agents.Tools;

/// <summary>
/// "write" - creates a new file or completely overwrites an existing one. For
/// small, targeted changes to existing files prefer <see cref="EditFileTool"/>;
/// this tool is for brand-new files or full rewrites.
///
/// A new file is written exactly as given. When overwriting, the file's existing line endings
/// (CRLF or LF) and BOM are kept, so rewriting a Windows file doesn't turn every line of it
/// into a change in a diff.
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
        var format = TextFileFormat.Default;
        var keptLineEndings = string.Empty;
        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (existed)
            {
                format = TextFileFormat.Read(fullPath).Format;
                if (format.NewLineKnown)
                {
                    var converted = TextFileFormat.WithNewLine(content, format.NewLine);
                    if (converted != content)
                    {
                        keptLineEndings = $" Kept the file's {(format.UsesCrLf ? "CRLF" : "LF")} line endings.";
                        content = converted;
                    }
                }
            }

            format.Write(fullPath, content);
        }
        catch (Exception ex)
        {
            return $"Error: could not write '{path}': {ex.Message}";
        }

        var lineCount = content.Length == 0 ? 0 : content.Split('\n').Length;
        var verb = existed ? "Overwrote" : "Created";
        return $"{verb} {ToolPaths.Display(rootDirectory, fullPath)} ({content.Length} chars, {lineCount} lines)." +
               keptLineEndings;
    }
}
