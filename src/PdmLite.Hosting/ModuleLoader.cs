using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Modularity;

namespace PdmLite.Hosting;

public enum ModuleState
{
    Loaded,
    Disabled,
    UnsupportedHost,
    MissingDependency,
    Failed,
}

/// <summary>Результат загрузки модуля — для журнала и окна «Модули».</summary>
public sealed record ModuleStatus(ModuleInfo Info, ModuleState State, string? Reason = null)
{
    public bool IsLoaded => State == ModuleState.Loaded;
}

/// <summary>Поиск модулей и определение порядка их загрузки.</summary>
public sealed class ModuleLoader(ILogger logger)
{
    public const string AssemblyPattern = "PdmLite.Modules.*.dll";

    /// <summary>Находит модули в сборках <c>PdmLite.Modules.*.dll</c> указанной папки.</summary>
    public IReadOnlyList<IPdmModule> Discover(string folder)
    {
        // Загружаем в тот же контекст, что и ядро, иначе IPdmModule из модуля
        // и из ядра окажутся разными типами (актуально для плагина внутри Inventor).
        var context = AssemblyLoadContext.GetLoadContext(typeof(IPdmModule).Assembly) ?? AssemblyLoadContext.Default;
        var modules = new List<IPdmModule>();

        foreach (var file in Directory.EnumerateFiles(folder, AssemblyPattern).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var name = AssemblyName.GetAssemblyName(file);
                var assembly = context.Assemblies.FirstOrDefault(a => AssemblyName.ReferenceMatchesDefinition(a.GetName(), name))
                               ?? context.LoadFromAssemblyPath(file);
                modules.AddRange(CreateModules(assembly));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Не удалось загрузить сборку модуля {File}", file);
            }
        }

        return modules;
    }

    private IEnumerable<IPdmModule> CreateModules(Assembly assembly)
    {
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || !typeof(IPdmModule).IsAssignableFrom(type))
                continue;

            IPdmModule? module = null;
            try
            {
                module = (IPdmModule?)Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Не удалось создать модуль {Type}", type.FullName);
            }

            if (module is not null)
                yield return module;
        }
    }

    /// <summary>
    /// Отбирает модули для хоста и упорядочивает их так, чтобы зависимости шли раньше зависимых.
    /// </summary>
    public (IReadOnlyList<IPdmModule> Ordered, IReadOnlyList<ModuleStatus> Statuses) Resolve(
        IEnumerable<IPdmModule> candidates, HostKind host, ModulesConfig config)
    {
        var statuses = new Dictionary<string, ModuleStatus>(StringComparer.OrdinalIgnoreCase);
        var available = new Dictionary<string, IPdmModule>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in candidates)
        {
            var info = module.Info;
            if (available.ContainsKey(info.Id) || statuses.ContainsKey(info.Id))
            {
                logger.LogWarning("Модуль {Id} найден повторно, пропущен", info.Id);
                continue;
            }

            if (!config.IsEnabled(info.Id))
                statuses[info.Id] = new ModuleStatus(info, ModuleState.Disabled, "Отключён в modules.json");
            else if ((info.SupportedHosts & host) == 0)
                statuses[info.Id] = new ModuleStatus(info, ModuleState.UnsupportedHost, $"Не работает в хосте {host}");
            else
                available[info.Id] = module;
        }

        var ordered = new List<IPdmModule>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Visit(IPdmModule module)
        {
            var id = module.Info.Id;
            if (statuses.TryGetValue(id, out var known))
                return known.IsLoaded;
            if (!visiting.Add(id))
            {
                statuses[id] = new ModuleStatus(module.Info, ModuleState.MissingDependency, "Циклическая зависимость");
                return false;
            }

            foreach (var dependency in module.Info.Dependencies)
            {
                if (!available.TryGetValue(dependency, out var dependencyModule) || !Visit(dependencyModule))
                {
                    visiting.Remove(id);
                    statuses[id] = new ModuleStatus(module.Info, ModuleState.MissingDependency, $"Нет модуля '{dependency}'");
                    return false;
                }
            }

            visiting.Remove(id);
            statuses[id] = new ModuleStatus(module.Info, ModuleState.Loaded);
            ordered.Add(module);
            return true;
        }

        foreach (var module in available.Values.OrderBy(m => m.Info.Id, StringComparer.OrdinalIgnoreCase))
            Visit(module);

        foreach (var status in statuses.Values.Where(s => !s.IsLoaded))
            logger.LogWarning("Модуль {Id} не загружен: {Reason}", status.Info.Id, status.Reason);

        return (ordered, statuses.Values.OrderBy(s => s.Info.Id, StringComparer.OrdinalIgnoreCase).ToList());
    }
}
