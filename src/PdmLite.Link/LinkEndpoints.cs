namespace PdmLite.Link;

/// <summary>Конечные точки связи. Канал создаётся отдельно для каждого пользователя Windows.</summary>
public static class LinkEndpoints
{
    /// <summary>Сервер в плагине Inventor.</summary>
    public const string Inventor = "Inventor";

    /// <summary>Сервер в PdmLite Explorer.</summary>
    public const string Explorer = "Explorer";

    public static string PipeName(string endpoint) => $"PdmLite.{endpoint}.{Environment.UserName}";
}
