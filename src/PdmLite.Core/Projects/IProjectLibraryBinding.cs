namespace PdmLite.Core.Projects;

public enum LibraryBindingState
{
    /// <summary>В Inventor нет активного проекта.</summary>
    NoProject,

    /// <summary>Библиотека подключена с правильной папкой.</summary>
    Connected,

    /// <summary>Библиотеки с таким именем в проекте нет.</summary>
    Missing,

    /// <summary>Библиотека с таким именем есть, но указывает на другую папку.</summary>
    DifferentFolder,
}

public sealed record LibraryBinding(LibraryBindingState State, string? ProjectName, string? CurrentFolder = null);

/// <summary>
/// Подключение папки библиотеки к активному проекту Inventor (раздел «Библиотеки» .ipj).
/// Есть только внутри Inventor.
/// </summary>
public interface IProjectLibraryBinding
{
    Task<LibraryBinding> CheckAsync(string libraryName, string folder, CancellationToken cancellationToken = default);

    /// <summary>Добавляет (или исправляет) библиотеку в активном проекте.</summary>
    Task ConnectAsync(string libraryName, string folder, CancellationToken cancellationToken = default);
}
