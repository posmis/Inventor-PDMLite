using System.Runtime.InteropServices;

namespace ShortenNames;

/// <summary>
/// Ссылки документов через Apprentice (Inventor или Inventor View), позднее связывание — без сборок Inventor.
/// Документ с путём длиннее, чем принимает Inventor, читается через временную копию с коротким путём.
/// </summary>
internal sealed class ApprenticeLinks : IDocumentLinks, IDisposable
{
    /// <summary>Путь, длиннее которого Inventor может не открыть файл.</summary>
    private const int SafePath = 245;

    private static readonly string[] ProgIds = ["Inventor.ApprenticeServer", "Inventor.ApprenticeServerComponent"];
    private static readonly Guid ApprenticeClsid = new("C343ED84-A129-11D3-B799-0060B0F159EF");

    private dynamic? _server;

    private ApprenticeLinks(object server) => _server = server;

    public static ApprenticeLinks Start()
    {
        var errors = new List<string>();
        var types = ProgIds.Select(id => (Name: id, Type: Type.GetTypeFromProgID(id)))
            .Append(("CLSID " + ApprenticeClsid, Type.GetTypeFromCLSID(ApprenticeClsid)));
        foreach (var (name, type) in types)
        {
            if (type is null)
            {
                errors.Add($"{name}: не зарегистрирован");
                continue;
            }
            try
            {
                return new ApprenticeLinks(Activator.CreateInstance(type)!);
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }
        throw new InvalidOperationException("Apprentice не запустился (" + string.Join("; ", errors) + "). Нужен установленный Inventor или Inventor View.");
    }

    public IReadOnlyList<string> References(string path)
    {
        string? proxy = null;
        var open = path;
        if (path.Length > SafePath)
        {
            // Inventor не открывает такой путь — читаем копию с коротким путём (ссылки хранятся в самом файле).
            var folder = Path.Combine(Path.GetTempPath(), "pdmlite-sn");
            Directory.CreateDirectory(folder);
            proxy = open = Path.Combine(folder, Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(path));
            File.Copy(path, proxy);
        }
        try
        {
            return WithDocument(open, document =>
            {
                var result = new List<string>();
                List<dynamic> descriptors = Descriptors(document);
                foreach (var descriptor in descriptors)
                {
                    string? name = Name(descriptor);
                    if (name is not null)
                        result.Add(name);
                }
                return result;
            });
        }
        finally
        {
            if (proxy is not null)
                TryDelete(proxy);
        }
    }

    public int Replace(string path, IReadOnlyDictionary<string, string> byPath, IReadOnlyDictionary<string, string> byName)
    {
        if (path.Length > SafePath)
            throw new InvalidOperationException($"путь документа слишком длинный ({path.Length}) — сократите и его");
        return WithDocument(path, document =>
        {
            var replaced = 0;
            List<dynamic> descriptors = Descriptors(document);
            foreach (var descriptor in descriptors)
            {
                string? target = null;
                // Явные типы: с dynamic-аргументами результат тоже был бы dynamic.
                IEnumerable<string> names = Names(descriptor);
                foreach (var candidate in names)
                {
                    if (byPath.TryGetValue(candidate, out target))
                        break;
                    if (byName.TryGetValue(Path.GetFileName(candidate), out target))
                        break;
                }
                if (target is null)
                    continue;
                descriptor.ReplaceReference(target);
                replaced++;
            }
            if (replaced > 0)
            {
                dynamic save = _server!.FileSaveAs;
                save.AddFileToSave(document, (string)document.FullFileName);
                save.ExecuteSave();
            }
            return replaced;
        });
    }

    private T WithDocument<T>(string path, Func<dynamic, T> work)
    {
        dynamic document = _server!.Open(path);
        try
        {
            return work(document);
        }
        finally
        {
            try
            {
                document.Close();
            }
            catch (COMException)
            {
            }
            Marshal.ReleaseComObject(document);
        }
    }

    private static List<dynamic> Descriptors(dynamic document)
    {
        var result = new List<dynamic>();
        dynamic descriptors = document.File.ReferencedFileDescriptors;
        int count = descriptors.Count;
        for (var i = 1; i <= count; i++)
        {
            object item;
            try
            {
                item = descriptors[i];
            }
            catch (Exception)
            {
                item = descriptors.Item(i);
            }
            result.Add(item);
        }
        return result;
    }

    /// <summary>Как ссылка записана: полный путь, относительное или логическое имя (что есть).</summary>
    private static IEnumerable<string> Names(dynamic descriptor)
    {
        foreach (var read in new Func<string?>[]
                 {
                     () => (string?)descriptor.FullFileName,
                     () => (string?)descriptor.RelativeFileName,
                     () => (string?)descriptor.LogicalFileName,
                 })
        {
            string? value;
            try
            {
                value = read();
            }
            catch (Exception)
            {
                value = null; // у потерянной или слишком длинной ссылки свойства может не быть
            }
            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
    }

    private static string? Name(dynamic descriptor) => ((IEnumerable<string>)Names(descriptor)).FirstOrDefault();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_server is null)
            return;
        try
        {
            _server.Close();
        }
        catch (Exception)
        {
        }
        Marshal.ReleaseComObject(_server);
        _server = null;
    }
}
