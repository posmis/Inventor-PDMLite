namespace PdmLite.Core.Documents;

/// <summary>Деталь или подсборка, входящая в сборку (со всеми уровнями вложенности).</summary>
/// <param name="Path">Полный путь файла компонента.</param>
/// <param name="PartNumber">Обозначение из свойств (Part Number).</param>
/// <param name="IsContentCenter">Компонент из Content Center Inventor.</param>
/// <param name="Count">Сколько раз компонент входит в сборку.</param>
/// <param name="IsMissing">Файл компонента не найден (ссылка потеряна, например проект перенесли с другого компьютера).</param>
public sealed record AssemblyComponentInfo(string Path, string? PartNumber, bool IsContentCenter, int Count, bool IsMissing = false);

public sealed record AssemblyInspection(string AssemblyPath, IReadOnlyList<AssemblyComponentInfo> Components);

/// <summary>Заменить все вхождения файла <paramref name="OldPath"/> на <paramref name="NewPath"/>.</summary>
public sealed record ComponentReplacement(string OldPath, string NewPath);

/// <param name="Replaced">Сколько замен выполнено (по сборкам).</param>
/// <param name="ModifiedAssemblies">Сборки, в которых что-то заменено (нужно сохранить).</param>
/// <param name="BackupFolder">Куда сохранены копии этих сборок до замены.</param>
/// <param name="Problems">Что заменить не удалось и почему.</param>
public sealed record ReplacementResult(int Replaced, IReadOnlyList<string> ModifiedAssemblies, string? BackupFolder, IReadOnlyList<string> Problems);

/// <summary>Выделенные в сборке компоненты.</summary>
/// <param name="Selected">Файлы выделенных компонентов (каждый — один раз).</param>
/// <param name="Components">Для выделенных подсборок — файлы всех их компонентов, на всех уровнях.</param>
/// <param name="Unsaved">Из них — открытые с несохранёнными изменениями.</param>
public sealed record ComponentSelection(IReadOnlyList<string> Selected, IReadOnlyList<string> Components, IReadOnlyList<string> Unsaved, string? AssemblyPath = null);

/// <summary>Состав активной сборки Inventor и замена компонентов в ней. Есть только внутри Inventor.</summary>
public interface IAssemblyComponents
{
    /// <summary>Компоненты активной сборки. Если активный документ — не сборка, бросает <see cref="InvalidOperationException"/>.</summary>
    Task<AssemblyInspection> InspectActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Заменяет компоненты во всех сборках активной сборки (включая подсборки). Перед заменой
    /// копирует изменяемые сборки в папку резервных копий. Сохраняет пользователь.
    /// </summary>
    Task<ReplacementResult> ReplaceAsync(IReadOnlyList<ComponentReplacement> replacements, CancellationToken cancellationToken = default, string? expectedAssemblyPath = null);

    /// <summary>Компоненты, выделенные в активной сборке. Если активный документ — не сборка, бросает <see cref="InvalidOperationException"/>.</summary>
    Task<ComponentSelection> GetSelectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Имена вхождений в обозревателе, которые не соответствуют файлу (остались после замены, например
    /// из Content Center): для компонентов <paramref name="componentPaths"/> во всех сборках активной сборки.
    /// </summary>
    /// <param name="apply"><c>false</c> — только список; <c>true</c> — переименовать (сохраняет пользователь).</param>
    Task<IReadOnlyList<OccurrenceRename>> FixOccurrenceNamesAsync(IReadOnlyCollection<string> componentPaths, bool apply,
        CancellationToken cancellationToken = default, string? expectedAssemblyPath = null);

    /// <summary>Сохраняет открытые в Inventor документы (по полным путям).</summary>
    Task SaveDocumentsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
}
