using PdmLite.Data;

namespace PdmLite.Library.Tests;

public class DatabaseSettingsTests
{
    [Fact]
    public void Password_RoundTrips_AndIsNotStoredAsPlainText()
    {
        var settings = new DatabaseSettings();
        settings.SetPassword("Пароль-123");

        Assert.True(settings.IsConfigured);
        Assert.DoesNotContain("Пароль", settings.ProtectedPassword);
        Assert.Equal("Пароль-123", settings.GetPassword());
        Assert.Contains("Password=Пароль-123", settings.BuildConnectionString());
    }
}
