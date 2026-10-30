using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Npgsql;

namespace PdmLite.Data;

/// <summary>
/// Подключение к PostgreSQL (<c>%AppData%\PdmLite\database.json</c>).
/// Пароль хранится зашифрованным для текущего пользователя Windows (DPAPI) —
/// в том же формате, что даёт PowerShell <c>ConvertFrom-SecureString</c>.
/// </summary>
public sealed class DatabaseSettings
{
    public const string Name = "database";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "pdmlite";
    public string Username { get; set; } = "pdmlite";

    /// <summary>Зашифрованный пароль (hex). Не заполнять вручную — см. <see cref="SetPassword"/>.</summary>
    public string? ProtectedPassword { get; set; }

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(Database)
                                && !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(ProtectedPassword);

    public string GetPassword()
    {
        if (string.IsNullOrEmpty(ProtectedPassword))
            return string.Empty;
        var bytes = Convert.FromHexString(ProtectedPassword);
        if (OperatingSystem.IsWindows())
            bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        return Encoding.Unicode.GetString(bytes);
    }

    public void SetPassword(string password)
    {
        var bytes = Encoding.Unicode.GetBytes(password);
        if (OperatingSystem.IsWindows())
            bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        // Вне Windows (тесты, CI) шифрования нет — только кодирование.
        ProtectedPassword = Convert.ToHexString(bytes);
    }

    public string BuildConnectionString() => new NpgsqlConnectionStringBuilder
    {
        Host = Host,
        Port = Port,
        Database = Database,
        Username = Username,
        Password = GetPassword(),
        Timeout = 10,
    }.ConnectionString;
}
