using PdmLite.Link;

namespace PdmLite.Explorer;

internal static class ExplorerInstanceForwarder
{
    public static async Task<bool> ForwardAsync(StartupArgs args, string endpoint = LinkEndpoints.Explorer, CancellationToken ct = default)
    {
        await using var client = new LinkClient(endpoint);
        if (!await client.TryConnectAsync(TimeSpan.FromMilliseconds(300), ct)) return false;
        try
        {
            await LinkHandshake.VerifyAsync(client, "PdmLite.Explorer", ct);
            if (args.RevealPath is not null)
                await client.RequestAsync(LinkProtocol.Requests.RevealDocument, new RevealArgs(args.RevealPath, args.ProjectPath), cancellationToken: ct);
            else if (args.ProjectPath is not null)
                await client.RequestAsync(LinkProtocol.Requests.OpenProject, new ProjectArgs(args.ProjectPath), cancellationToken: ct);
            return true;
        }
        // A mismatched running instance must not turn into a second Explorer process.
        catch (LinkCompatibilityException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return false; }
    }
}
