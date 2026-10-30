namespace PdmLite.Core.Modularity;

/// <param name="Id">Уникальный идентификатор модуля (используется в modules.json).</param>
/// <param name="Name">Отображаемое имя.</param>
/// <param name="Version">Версия модуля.</param>
/// <param name="SupportedHosts">Хосты, в которых модуль может работать.</param>
/// <param name="DependsOn">Идентификаторы модулей, без которых этот модуль не загружается.</param>
public sealed record ModuleInfo(
    string Id,
    string Name,
    Version Version,
    HostKind SupportedHosts = HostKind.All,
    IReadOnlyList<string>? DependsOn = null)
{
    public IReadOnlyList<string> Dependencies => DependsOn ?? [];
}
