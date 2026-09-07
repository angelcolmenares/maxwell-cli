using System.Reflection;
using System.Runtime.Loader;

namespace Maxwell.Agents.Plugins;

/// <summary>
/// One <see cref="AssemblyLoadContext"/> per plugin, so plugins can bring their
/// own dependency versions (e.g. a different HTTP client or JSON library) without
/// clashing with each other or with Maxwell's own dependencies, and so a plugin
/// can be unloaded later (<c>isCollectible: true</c>) without restarting the process.
///
/// Naive isolation would break the plugin contract, though: if
/// <c>Maxwell.Agents.dll</c> loads once into the default context and again into
/// each plugin's context, a plugin's <see cref="IMaxwellPlugin"/> implementation
/// would implement a *different* <c>IMaxwellPlugin</c> type than the one
/// <see cref="PluginLoader"/> checks against, and every cast would fail at
/// runtime. The fix is <see cref="SharedAssemblyNames"/>: for exactly those
/// "contract" assemblies, <see cref="Load"/> returns null, which tells the
/// runtime to fall back to the default context - so the plugin and the host
/// always share one identical copy of the types they hand back and forth
/// (IMaxwellPlugin, IAgentProvider, AITool, AIAgent, ...). Everything else the
/// plugin references resolves privately via <see cref="AssemblyDependencyResolver"/>,
/// which reads the plugin's own .deps.json and never touches Maxwell's directory.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Maxwell.Agents",
        "Microsoft.Extensions.AI",
        "Microsoft.Extensions.AI.Abstractions",
        "Microsoft.Agents.AI",
        "Microsoft.Agents.AI.Abstractions",
    };

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginId, string mainAssemblyPath)
        : base(name: $"MaxwellPlugin:{pluginId}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && SharedAssemblyNames.Contains(assemblyName.Name))
        {
            // Returning null here means "not found in this context" - the runtime
            // then resolves it from the default AssemblyLoadContext instead, which
            // already has the host's copy loaded.
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
