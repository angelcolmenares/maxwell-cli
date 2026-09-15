using System.Text.Json;
using Maxwell.Agents.Hooks;
using Maxwell.Agents.Providers;
using Microsoft.Extensions.AI;

namespace Maxwell.Agents.Plugins;

/// <summary>Result of attempting to load one plugin folder, for `maxwell-cli plugins list`-style reporting.</summary>
public sealed record LoadedPlugin(string Id, string Version, string Directory, bool Succeeded, IReadOnlyList<string> Errors);

/// <summary>
/// Scans {HomeDirectory}/.maxwell/plugins/*/plugin.json, loads each plugin's
/// entry assembly into its own <see cref="PluginLoadContext"/>, and gives every
/// <see cref="IMaxwellPlugin"/> type it finds a chance to register providers,
/// tools, and hooks.
///
/// Loading is best-effort per plugin: a malformed manifest, a missing assembly,
/// or an exception thrown from a plugin's Configure* method is recorded against
/// that plugin and does not stop the others from loading or prevent Maxwell from
/// starting - the same "additive, best-effort" philosophy <see cref="Providers.SkillLoader"/>
/// already uses for skills.
/// </summary>
public sealed class PluginLoader(MaxwellPaths paths)
{
    public IReadOnlyList<LoadedPlugin> LoadAll(AgentProviderRegistry providers, ICollection<AITool> pluginTools, HookPipeline hooks)
    {
        var results = new List<LoadedPlugin>();

        if (!Directory.Exists(paths.PluginsDir))
        {
            return results;
        }

        foreach (var pluginDir in Directory.EnumerateDirectories(paths.PluginsDir))
        {
            results.Add(LoadOne(pluginDir, providers, pluginTools, hooks));
        }

        return results;
    }

    private static LoadedPlugin LoadOne(string pluginDir, AgentProviderRegistry providers, ICollection<AITool> pluginTools, HookPipeline hooks)
    {
        var folderName = Path.GetFileName(pluginDir);
        var manifestFile = Path.Combine(pluginDir, "plugin.json");
        var errors = new List<string>();

        if (!File.Exists(manifestFile))
        {
            return new LoadedPlugin(folderName, "0.0.0", pluginDir, false, [$"Missing plugin.json in '{pluginDir}'."]);
        }

        PluginManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestFile), MaxwellJson.Options)
                       ?? throw new InvalidOperationException("plugin.json deserialized to null.");
        }
        catch (Exception ex)
        {
            return new LoadedPlugin(folderName, "0.0.0", pluginDir, false, [$"Could not read plugin.json: {ex.Message}"]);
        }

        var id = string.IsNullOrWhiteSpace(manifest.Id) ? folderName : manifest.Id;

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            return new LoadedPlugin(id, manifest.Version, pluginDir, false, ["plugin.json is missing \"entryAssembly\"."]);
        }

        var assemblyPath = Path.Combine(pluginDir, manifest.EntryAssembly);
        if (!File.Exists(assemblyPath))
        {
            return new LoadedPlugin(id, manifest.Version, pluginDir, false, [$"Entry assembly not found: {assemblyPath}"]);
        }

        try
        {
            var context = new PluginLoadContext(id, assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);

            var pluginTypes = assembly.GetTypes()
                .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IMaxwellPlugin).IsAssignableFrom(t))
                .ToList();

            if (pluginTypes.Count == 0)
            {
                errors.Add($"No public type implementing IMaxwellPlugin was found in '{manifest.EntryAssembly}'.");
            }

            foreach (var type in pluginTypes)
            {
                InstantiateAndConfigure(type, providers, pluginTools, hooks, errors);
            }
        }
        catch (Exception ex)
        {
            // Covers assembly load failures (bad IL, missing private deps the
            // resolver couldn't find, wrong TFM, ...) - one bad plugin must not
            // take the whole CLI down.
            errors.Add($"Failed to load '{manifest.EntryAssembly}': {ex.Message}");
        }

        return new LoadedPlugin(id, manifest.Version, pluginDir, errors.Count == 0, errors);
    }

    private static void InstantiateAndConfigure(
        Type type, AgentProviderRegistry providers, ICollection<AITool> pluginTools, HookPipeline hooks, List<string> errors)
    {
        IMaxwellPlugin plugin;
        try
        {
            if (Activator.CreateInstance(type) is not IMaxwellPlugin created)
            {
                errors.Add($"'{type.FullName}' could not be instantiated as IMaxwellPlugin.");
                return;
            }

            plugin = created;
        }
        catch (MissingMethodException)
        {
            errors.Add($"'{type.FullName}' has no public parameterless constructor.");
            return;
        }
        catch (Exception ex)
        {
            errors.Add($"'{type.FullName}' threw during construction: {ex.Message}");
            return;
        }

        try
        {
            plugin.ConfigureProviders(providers);
        }
        catch (Exception ex)
        {
            errors.Add($"'{plugin.Id}'.ConfigureProviders threw: {ex.Message}");
        }

        try
        {
            var registry = new CollectingToolRegistry();
            plugin.ConfigureTools(registry);
            foreach (var tool in registry.Tools)
            {
                pluginTools.Add(tool);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"'{plugin.Id}'.ConfigureTools threw: {ex.Message}");
        }

        try
        {
            plugin.ConfigureHooks(hooks);
        }
        catch (Exception ex)
        {
            errors.Add($"'{plugin.Id}'.ConfigureHooks threw: {ex.Message}");
        }
    }

    private sealed class CollectingToolRegistry : IPluginToolRegistry
    {
        public List<AITool> Tools { get; } = [];

        public void Register(AITool tool) => Tools.Add(tool);
    }
}
