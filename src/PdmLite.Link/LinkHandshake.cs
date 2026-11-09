using System.Text.Json;

namespace PdmLite.Link;

public sealed class LinkCompatibilityException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

public static class LinkHandshake
{
    public static async Task<PingResult> VerifyAsync(LinkClient client, string application, CancellationToken ct = default)
    {
        PingResult? ping;
        try
        {
            ping = await client.RequestAsync<PingResult>(LinkProtocol.Requests.Ping, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or LinkRemoteException)
        {
            throw new LinkCompatibilityException("Не удалось проверить совместимость PdmLite. Обновите Explorer и плагин Inventor, затем перезапустите их.", ex);
        }
        if (ping is null || !string.Equals(ping.Application, application, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(ping.Version) || ping.ProtocolVersion != LinkProtocol.Version)
            throw new LinkCompatibilityException($"Несовместимая версия PdmLite ({application}). "
                + $"Ожидался протокол {LinkProtocol.Version}, получен {ping?.ProtocolVersion.ToString() ?? "пустой ответ"}. "
                + "Обновите Explorer и плагин Inventor, затем перезапустите их.");
        return ping;
    }
}
