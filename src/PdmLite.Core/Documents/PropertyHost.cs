using System.Text.Encodings.Web;
using System.Text.Json;

namespace PdmLite.Core.Documents;

/// <summary>Запрос к процессу свойств: операция и её данные.</summary>
/// <param name="Op">read | readNoGeometry | write | replace.</param>
public sealed record PropertyHostRequest(string Op, string Path, DocumentPropertyChanges? Changes = null, Dictionary<string, string>? Map = null);

/// <summary>Ответ: свойства (для чтения) или текст ошибки.</summary>
public sealed record PropertyHostResponse(bool Ok, DocumentProperties? Properties = null, string? Error = null);

/// <summary>
/// Обмен с отдельным процессом, который читает и пишет свойства файлов через Apprentice (внутри Inventor
/// Apprentice не работает, а Inventor API не может записать копию файла Content Center, открытого в сборке).
/// Одна строка JSON — один запрос или ответ.
/// </summary>
public static class PropertyHostProtocol
{
    public static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Сторона процесса свойств: читает запросы, пока вход не закрыт, выполняет их хранилищем и пишет ответы.
    /// Ошибка одного запроса — ответ с ошибкой, обработка продолжается.
    /// </summary>
    public static async Task ServeAsync(TextReader input, TextWriter output, IDocumentPropertyStore store, CancellationToken ct = default)
    {
        while (await input.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
                continue;
            PropertyHostResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<PropertyHostRequest>(line, Json) ?? throw new InvalidDataException("Пустой запрос.");
                response = request.Op switch
                {
                    "read" => new PropertyHostResponse(true, await store.ReadAsync(request.Path, ct).ConfigureAwait(false)),
                    "readNoGeometry" => new PropertyHostResponse(true, await store.ReadWithoutGeometryAsync(request.Path, ct).ConfigureAwait(false)),
                    "write" => await Done(store.WriteAsync(request.Path, request.Changes ?? new DocumentPropertyChanges(), ct)).ConfigureAwait(false),
                    "replace" => await Done(store.ReplaceReferencesAsync(request.Path,
                        new Dictionary<string, string>(request.Map ?? [], StringComparer.OrdinalIgnoreCase), ct)).ConfigureAwait(false),
                    _ => new PropertyHostResponse(false, Error: $"Неизвестная операция «{request.Op}».")
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                response = new PropertyHostResponse(false, Error: ex.Message);
            }

            await output.WriteLineAsync(JsonSerializer.Serialize(response, Json).AsMemory(), ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<PropertyHostResponse> Done(Task operation)
    {
        await operation.ConfigureAwait(false);
        return new PropertyHostResponse(true);
    }
}

/// <summary>
/// Сторона клиента: запросы по одному (ответы приходят по порядку). Ошибка процесса свойств —
/// <see cref="InvalidOperationException"/> с его текстом. Если ответ не дождались (тайм-аут или отмена),
/// он придёт позже и сбьёт порядок — клиент дальше не работает (<see cref="IOException"/>), нужен новый процесс.
/// </summary>
public sealed class PropertyHostClient(TextReader input, TextWriter output)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _broken;

    /// <summary>Порядок ответов сбит — клиент больше не пригоден.</summary>
    public bool IsBroken => _broken;

    public async Task<PropertyHostResponse> SendAsync(PropertyHostRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_broken)
                throw new IOException("Ответ на прошлый запрос не дождались — порядок ответов сбит, нужен новый процесс свойств.");
            await output.WriteLineAsync(JsonSerializer.Serialize(request, PropertyHostProtocol.Json).AsMemory(), ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            string? line;
            try
            {
                line = await input.ReadLineAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _broken = true;
                throw new TimeoutException($"Процесс свойств не ответил за {timeout.TotalSeconds:0} с.");
            }
            catch (OperationCanceledException)
            {
                _broken = true; // запрос ушёл, ответ придёт позже
                throw;
            }

            var response = line is null
                ? throw new IOException("Процесс свойств завершился.")
                : JsonSerializer.Deserialize<PropertyHostResponse>(line, PropertyHostProtocol.Json) ?? throw new InvalidDataException("Пустой ответ.");
            return response.Ok ? response : throw new InvalidOperationException(response.Error ?? "Ошибка процесса свойств.");
        }
        finally
        {
            _gate.Release();
        }
    }
}
