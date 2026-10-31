using System.IO.Pipes;
using PdmLite.Core.Documents;

namespace PdmLite.Core.Tests;

/// <summary>Обмен с процессом свойств: запросы, ответы, ошибки — через пару каналов, как через stdin/stdout.</summary>
public class PropertyHostTests
{
    private sealed class MemoryStore : IDocumentPropertyStore
    {
        public Dictionary<string, DocumentPropertyChanges> Written { get; } = [];
        public bool GeometryRead { get; private set; }
        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public Task<DocumentProperties> ReadAsync(string path, CancellationToken ct = default)
        {
            GeometryRead = true;
            return Task.FromResult(new DocumentProperties { PartNumber = "П-" + path, Volume = 1.5, References = ["a.ipt"] });
        }

        public Task<DocumentProperties> ReadWithoutGeometryAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(new DocumentProperties { PartNumber = "П-" + path });

        public Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken ct = default)
        {
            if (path == "сбой")
                throw new IOException("Файл занят");
            Written[path] = changes;
            return Task.CompletedTask;
        }

        public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string, string> map, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static (PropertyHostClient Client, Task Server, MemoryStore Store, StreamWriter ToServer) Start()
    {
        var store = new MemoryStore();
        var requests = new AnonymousPipeServerStream(PipeDirection.Out);
        var requestsIn = new AnonymousPipeClientStream(PipeDirection.In, requests.ClientSafePipeHandle);
        var responses = new AnonymousPipeServerStream(PipeDirection.Out);
        var responsesIn = new AnonymousPipeClientStream(PipeDirection.In, responses.ClientSafePipeHandle);

        var toServer = new StreamWriter(requests) { AutoFlush = true };
        var server = PropertyHostProtocol.ServeAsync(new StreamReader(requestsIn), new StreamWriter(responses), store);
        return (new PropertyHostClient(new StreamReader(responsesIn), toServer), server, store, toServer);
    }

    [Fact]
    public async Task Read_Write_Replace_AndErrors()
    {
        var (client, server, store, toServer) = Start();
        var timeout = TimeSpan.FromSeconds(10);

        var read = await client.SendAsync(new PropertyHostRequest("read", "Болт.ipt"), timeout);
        Assert.Equal("П-Болт.ipt", read.Properties!.PartNumber);
        Assert.Equal(1.5, read.Properties.Volume);
        Assert.Equal(["a.ipt"], read.Properties.References);

        var fast = await client.SendAsync(new PropertyHostRequest("readNoGeometry", "x"), timeout);
        Assert.Null(fast.Properties!.Volume);

        var changes = new DocumentPropertyChanges
        {
            PartNumber = "Винт A2.M2-6gx5.48.016 ГОСТ 17473-80",
            Custom = new Dictionary<string, string> { ["SPEC_Section"] = "StandardProducts" },
            RemoveContentCenterLink = true,
        };
        await client.SendAsync(new PropertyHostRequest("write", "copy.ipt", changes), timeout);
        Assert.Equal(changes.PartNumber, store.Written["copy.ipt"].PartNumber);
        Assert.Equal("StandardProducts", store.Written["copy.ipt"].Custom["SPEC_Section"]);
        Assert.True(store.Written["copy.ipt"].RemoveContentCenterLink);

        // Ошибка одного запроса не останавливает процесс.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new PropertyHostRequest("write", "сбой"), timeout));
        Assert.Contains("Файл занят", error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new PropertyHostRequest("unknown", "x"), timeout));
        await client.SendAsync(new PropertyHostRequest("replace", "a.iam", Map: new() { ["old"] = "new" }), timeout);

        toServer.Dispose(); // вход закрыт — процесс свойств завершает работу
        await server.WaitAsync(timeout);
    }
}
