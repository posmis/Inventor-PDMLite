using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdmLite.Core.UI;
using PdmLite.Data;
using PdmLite.Hosting;
using PdmLite.Library.Storage;

namespace PdmLite.Modules.Library.Settings;

public sealed partial class LibrarySettingsViewModel(
    IPdmDatabase database,
    JsonSettings<DatabaseSettings> databaseSettings,
    JsonSettings<LibrarySettings> librarySettings) : ObservableObject
{
    [ObservableProperty] private string _host = databaseSettings.Value.Host;
    [ObservableProperty] private string _port = databaseSettings.Value.Port.ToString();
    [ObservableProperty] private string _database1 = databaseSettings.Value.Database;
    [ObservableProperty] private string _username = databaseSettings.Value.Username;
    [ObservableProperty] private string _rootFolder = librarySettings.Value.RootFolder;

    [ObservableProperty] private string _testMessage = "";
    [ObservableProperty] private string? _testIcon;
    [ObservableProperty] private bool _isBusy;

    /// <summary>Пароль задаётся из PasswordBox (он не поддерживает привязку). Пусто — оставить прежний.</summary>
    public string Password { get; set; } = "";

    public bool HasSavedPassword => !string.IsNullOrEmpty(databaseSettings.Value.ProtectedPassword);

    [RelayCommand]
    private async Task TestAsync()
    {
        if (BuildSettings() is not { } settings)
            return;
        IsBusy = true;
        TestIcon = null;
        TestMessage = "Подключение…";
        var (ok, message) = await database.TestAsync(settings);
        TestIcon = ok ? IconKeys.StateOk : IconKeys.Error;
        TestMessage = message;
        IsBusy = false;
    }

    /// <summary>Сохраняет настройки. Возвращает текст ошибки или <c>null</c>.</summary>
    public string? Save()
    {
        if (BuildSettings() is not { } settings)
            return TestMessage;
        if (string.IsNullOrWhiteSpace(RootFolder))
            return "Укажите папку библиотеки.";

        try
        {
            Directory.CreateDirectory(RootFolder);
        }
        catch (Exception ex)
        {
            return $"Не удалось создать папку библиотеки: {ex.Message}";
        }

        databaseSettings.Value.Host = settings.Host;
        databaseSettings.Value.Port = settings.Port;
        databaseSettings.Value.Database = settings.Database;
        databaseSettings.Value.Username = settings.Username;
        databaseSettings.Value.ProtectedPassword = settings.ProtectedPassword;
        databaseSettings.Save();
        database.Configure(databaseSettings.Value);

        librarySettings.Value.RootFolder = Path.GetFullPath(RootFolder);
        librarySettings.Save();
        return null;
    }

    private DatabaseSettings? BuildSettings()
    {
        if (!int.TryParse(Port, out var port) || port is <= 0 or > 65535)
        {
            TestIcon = IconKeys.Error;
            TestMessage = "Порт — число от 1 до 65535.";
            return null;
        }

        var settings = new DatabaseSettings
        {
            Host = Host.Trim(),
            Port = port,
            Database = Database1.Trim(),
            Username = Username.Trim(),
            ProtectedPassword = databaseSettings.Value.ProtectedPassword,
        };
        if (Password.Length > 0)
            settings.SetPassword(Password);
        return settings;
    }
}
