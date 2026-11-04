using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PdmLite.Core.IO;
using PdmLite.Data;
using PdmLite.Hosting;
using PdmLite.Library.Backup;
using PdmLite.Library.Storage;
using PdmLite.Installation;

return await Setup.RunAsync(args);

internal static class Setup
{
    private static readonly JsonSerializerOptions Json = JsonSettings.Options;

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Установка поддерживается только на Windows.");
            if (args.Length != 2) throw new ArgumentException("Ожидались: папка приложения и папка библиотеки.");
            var lockFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PdmLite");
            Directory.CreateDirectory(lockFolder);
            using var installLock = new FileStream(Path.Combine(lockFolder, "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            foreach (var name in new[] { "Inventor", "PdmLite.Explorer" })
                if (Process.GetProcessesByName(name).Length > 0)
                    throw new InvalidOperationException("Закройте Inventor и PdmLite Explorer, затем повторите установку.");
            await InstallAsync(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
            Console.WriteLine("PdmLite настроен. Можно запускать Explorer.");
            return 0;
        }
        catch (Exception ex)
        {
            // Do not print connection strings, passwords or full process arguments.
            var message = ex is PostgresException pg ? $"Ошибка PostgreSQL ({pg.SqlState})." : ex.Message;
            Console.Error.WriteLine("Установка не завершена: " + message);
            Console.Error.WriteLine("Данные не удаляются. Устраните причину и запустите установщик повторно.");
            if (!Console.IsInputRedirected) { Console.WriteLine("Нажмите Enter, чтобы закрыть окно."); Console.ReadLine(); }
            return 1;
        }
    }

    private static async Task InstallAsync(string app, string root)
    {
        var settingsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PdmLite");
        var state = SetupState.Open(settingsFolder, root);
        if (state.Existing is { } existing)
        {
            Console.WriteLine("Сохраняем существующую базу, библиотеку и настройки.");
            var db = new PdmDatabase(existing, NullLoggerFactory.Instance);
            await db.MigrateAsync();
            RegisterAddIn(app);
            return;
        }
        var archive = Path.Combine(app, "Setup", "library.zip");
        if (!File.Exists(archive)) throw new FileNotFoundException("Не найден начальный архив библиотеки.");
        var pending = state.Pending!;
        Console.WriteLine("Проверка и настройка локального PostgreSQL...");
        var admin = pending.Admin;
        if (!await CanConnectAsync(admin))
        {
            var pg = JsonSerializer.Deserialize<PostgreSqlPackage>(File.ReadAllText(Path.Combine(app, "Setup", "postgresql.json")), Json)!;
            var installer = Path.Combine(app, "Setup", "postgresql.exe");
            if (!File.Exists(installer))
            {
                Console.WriteLine("Загрузка PostgreSQL. Требуется подключение к интернету...");
                installer = Path.Combine(Path.GetTempPath(), "pdmlite-pg-" + Guid.NewGuid().ToString("N") + ".exe");
                try { await DownloadAsync(pg, installer); await InstallPostgresAsync(app, pg, installer, admin.GetPassword()); }
                finally { File.Delete(installer); }
            }
            else await InstallPostgresAsync(app, pg, installer, admin.GetPassword());
            for (var attempt = 0; attempt < 30 && !await CanConnectAsync(admin); attempt++)
                await Task.Delay(1000);
            if (!await CanConnectAsync(admin)) throw new InvalidOperationException("Не удалось подключиться к локальному PostgreSQL на порту 55439. Проверьте службу pdmlite-postgresql-16 и занятость порта.");
        }
        await CreateDatabaseAsync(pending);
        var database = new PdmDatabase(pending.Database, NullLoggerFactory.Instance);
        var library = new LibrarySettings { RootFolder = root };
        var service = new LibraryBackupService(database, () => new LibraryStorage(root),
            () => new BackupOptions(library.BackupFolder, library.BackupKeep, library.BackupIntervalDays),
            NullLogger<LibraryBackupService>.Instance);
        if (!pending.Restored)
        {
            await database.MigrateAsync();
            await using var context = database.CreateContext();
            if (await context.LibraryItems.AnyAsync())
                throw new InvalidOperationException("В базе уже есть компоненты. Автоматическая замена запрещена. Восстановите архив вручную через PdmLite.");
            Console.WriteLine("Проверка и восстановление начальной библиотеки...");
            await service.RestoreAsync(archive);
            pending.Restored = true;
            state.Save();
        }
        RegisterAddIn(app);
        state.Complete(library);
    }

    private static async Task<bool> CanConnectAsync(DatabaseSettings settings)
    {
        try { await using var connection = new NpgsqlConnection(settings.BuildConnectionString()); await connection.OpenAsync(); return true; }
        catch (NpgsqlException) { return false; }
    }

    private static async Task CreateDatabaseAsync(PendingSetup pending)
    {
        await using var connection = new NpgsqlConnection(pending.Admin.BuildConnectionString());
        await connection.OpenAsync();
        await using var role = new NpgsqlCommand("SELECT EXISTS(SELECT FROM pg_roles WHERE rolname = 'pdmlite')", connection);
        if (!(bool)(await role.ExecuteScalarAsync())!)
        {
            // Generated hex only; never interpolate arbitrary installer/user input into SQL.
            var password = pending.Database.GetPassword();
            if (password.Length != 64 || password.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Неверный формат пароля установки.");
            await using var createRole = new NpgsqlCommand($"CREATE ROLE pdmlite LOGIN PASSWORD '{password}'", connection);
            await createRole.ExecuteNonQueryAsync();
        }
        await using var exists = new NpgsqlCommand("SELECT EXISTS(SELECT FROM pg_database WHERE datname = 'pdmlite')", connection);
        if (!(bool)(await exists.ExecuteScalarAsync())!)
        {
            await using var createDatabase = new NpgsqlCommand("CREATE DATABASE pdmlite OWNER pdmlite ENCODING 'UTF8'", connection);
            await createDatabase.ExecuteNonQueryAsync();
        }
    }

    private static async Task DownloadAsync(PostgreSqlPackage pg, string path)
    {
        if (!Uri.TryCreate(pg.Url, UriKind.Absolute, out var url) || url.Scheme != "https")
            throw new InvalidDataException("Требуется HTTPS для загрузки PostgreSQL.");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        await using var source = await client.GetStreamAsync(url);
        await using var target = File.Create(path);
        await source.CopyToAsync(target);
    }

    private static async Task InstallPostgresAsync(string app, PostgreSqlPackage pg, string installer, string password)
    {
        await using (var stream = File.OpenRead(installer))
            if (!Convert.ToHexString(await SHA256.HashDataAsync(stream)).Equals(pg.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма установщика PostgreSQL не совпала.");
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = true,
            Verb = "runas"
        };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(app, "Setup", "install-postgresql-machine.ps1"), "-Installer", installer, "-Sha256", pg.Sha256, "-Password", password })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить установку PostgreSQL.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("Установка PostgreSQL не завершена. Повторите установку и подтвердите запрос Windows.");
    }

    private static void RegisterAddIn(string app)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Autodesk\Inventor 2025\Addins");
        var template = File.ReadAllText(Path.Combine(app, "Setup", "PdmLite.addin.template"));
        var assembly = System.Security.SecurityElement.Escape(Path.Combine(app, "AddIn", "PdmLite.AddIn.dll"))!;
        AtomicFile.WriteAllText(Path.Combine(dir, "PdmLite.addin"), template.Replace("{ASSEMBLY_PATH}", assembly));
    }

    private sealed record PostgreSqlPackage(string Version, string Url, string Sha256);
}
