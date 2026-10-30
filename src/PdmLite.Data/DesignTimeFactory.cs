using Microsoft.EntityFrameworkCore.Design;

namespace PdmLite.Data;

/// <summary>
/// Для инструмента миграций (<c>dotnet ef migrations add …</c>).
/// Строка подключения — из переменной PDMLITE_DB, иначе локальная база по умолчанию.
/// </summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<PdmDbContext>
{
    public PdmDbContext CreateDbContext(string[] args) =>
        new(PdmDatabase.BuildOptions(Environment.GetEnvironmentVariable("PDMLITE_DB")
                                     ?? "Host=localhost;Database=pdmlite;Username=pdmlite;Password=pdmlite"));
}
