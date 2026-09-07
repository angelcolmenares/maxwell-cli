namespace Maxwell.Agents.Tools;

/// <summary>
/// Resolves the (possibly relative) paths that a model passes into the built-in
/// tools against a single, fixed root - the working directory Maxwell was
/// launched from - rather than <see cref="Environment.CurrentDirectory"/>, which
/// tool implementations must never rely on or mutate.
/// </summary>
internal static class ToolPaths
{
    public static string Resolve(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must not be empty.", nameof(path));
        }

        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(root, path));
    }

    /// <summary>Same as <see cref="Resolve"/> but for a display-friendly relative form when possible.</summary>
    public static string Display(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        return relative.StartsWith("..", StringComparison.Ordinal) ? fullPath : relative;
    }
}
