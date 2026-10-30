namespace PdmLite.Core.Modularity;

/// <summary>Приложение, в котором работают модули.</summary>
[Flags]
public enum HostKind
{
    None = 0,

    /// <summary>Плагин внутри Inventor.</summary>
    Inventor = 1,

    /// <summary>Внешнее приложение PdmLite Explorer.</summary>
    Explorer = 2,

    All = Inventor | Explorer,
}
