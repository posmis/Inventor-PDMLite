using System.Reflection;
using System.Runtime.Loader;

namespace PdmLite.AddIn;

/// <summary>
/// Изолированный контекст загрузки плагина. Сборки из папки core (по их .deps.json)
/// загружаются здесь, со своими версиями. Общими с Inventor остаются только
/// Inventor API и сборки .NET/WPF — их контекст отдаёт стандартному контексту.
/// </summary>
internal sealed class AddInLoadContext : AssemblyLoadContext
{
    /// <summary>Сборки, которые обязаны быть общими с Inventor (типы COM-интерфейсов).</summary>
    private static readonly HashSet<string> SharedWithHost = new(StringComparer.OrdinalIgnoreCase)
    {
        "Autodesk.Inventor.Interop",
        "stdole",
        "PdmLite.AddIn",
    };

    private static AddInLoadContext? _instance;

    private readonly AssemblyDependencyResolver _resolver;

    private AddInLoadContext(string mainAssemblyPath) : base("PdmLite", isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    /// <summary>
    /// Контекст создаётся один раз за сеанс Inventor: повторная загрузка плагина
    /// (выгрузили и загрузили в «Надстройках») использует уже загруженные сборки.
    /// </summary>
    public static AddInLoadContext Create(string mainAssemblyPath)
    {
        if (_instance is not null)
            return _instance;

        var context = _instance = new AddInLoadContext(mainAssemblyPath);

        // WPF и XAML ищут сборки по имени через стандартный контекст
        // (pack://…/PdmLite.UI.Shared;component/…, clr-namespace;assembly=PdmLite.Core).
        // Сборки PdmLite отдаём из контекста плагина, чтобы не было второй копии.
        Default.Resolving += (_, name) =>
            name.Name is { } n && n.StartsWith("PdmLite.", StringComparison.OrdinalIgnoreCase) && !SharedWithHost.Contains(n)
                ? context.LoadFromAssemblyName(name)
                : null;

        return context;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null || SharedWithHost.Contains(assemblyName.Name))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null && File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
