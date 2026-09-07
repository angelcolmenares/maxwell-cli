namespace Maxwell.Agents.Plugins;

/// <summary>
/// {HomeDirectory}/.maxwell/plugins/{folder}/plugin.json, e.g.:
/// { "id": "maxwell-anthropic", "version": "0.1.0", "entryAssembly": "Maxwell.Plugin.Anthropic.dll" }
///
/// The folder name is just an install convenience; <see cref="Id"/> is the
/// identity Maxwell actually uses (in logs, in `plugins list`, and for last-one-
/// wins provider overrides), so it should be stable across reinstalls/renames.
/// </summary>
public sealed class PluginManifest
{
    public string Id { get; set; } = string.Empty;

    public string Version { get; set; } = "0.0.0";

    /// <summary>File name of the plugin's entry assembly, relative to the plugin's own folder.</summary>
    public string EntryAssembly { get; set; } = string.Empty;

    /// <summary>
    /// Optional lowest Maxwell host version this plugin is known to work with.
    /// Not enforced yet (Maxwell doesn't currently stamp its own version anywhere
    /// machine-readable) - reserved so PluginLoader can start checking it once it does.
    /// </summary>
    public string? MinHostVersion { get; set; }
}
