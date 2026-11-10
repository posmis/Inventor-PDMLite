namespace PdmLite.Core.Documents;

/// <summary>Проверяется в том же UI-вызове, что и изменение COM-документа.</summary>
public static class AssemblyTarget
{
    public static void Verify(string? expected, string current, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(expected))
            throw new InvalidOperationException("Сначала сохраните сборку и выполните проверку заново.");
        if (string.IsNullOrWhiteSpace(current) || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(expected), Path.GetFullPath(current)))
            throw new InvalidOperationException("Активная сборка изменилась. Вернитесь к исходной сборке или выполните проверку заново.");
    }
}
