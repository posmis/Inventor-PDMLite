using System.Runtime.InteropServices;
using System.Text;

namespace CollectComponents;

/// <summary>
/// Сбор файлов Inventor по списку имён — в одну папку, вместе с зависимостями сборок на всех уровнях.
/// Отдельная утилита (не часть PdmLite): список — из буфера обмена или файла, поиск — по имени только
/// в указанной папке, зависимости — через Apprentice (Inventor или Inventor View). Ничего не копирует,
/// пока пользователь не подтвердит.
/// </summary>
internal static class Program
{
    /// <summary>Журнал: всё, что видно в окне, — ещё и в файл (чтобы прислать при проблеме).</summary>
    internal static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdmLite", "logs",
        $"collect-components-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    [STAThread] // буфер обмена, окна выбора папок и Apprentice — в однопоточном апартаменте
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        var log = new StreamWriter(LogPath, append: false, new UTF8Encoding(true)) { AutoFlush = true };
        Console.SetOut(new TeeWriter(Console.Out, log));
        Console.WriteLine($"Журнал: {LogPath}");
        try
        {
            return Run(Options.Parse(args));
        }
        catch (Exception ex)
        {
            Error($"Ошибка: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.WriteLine();
            Console.WriteLine($"Полный журнал: {LogPath}");
            Console.WriteLine("Нажмите Enter, чтобы закрыть окно.");
            Console.ReadLine();
            log.Dispose();
        }
    }

    private static int Run(Options options)
    {
        Title("Сбор файлов Inventor по списку");

        // 1. Список
        var names = Collector.ParseNames(options.List is { } list ? File.ReadAllLines(list) : ReadClipboard());
        if (names.Count == 0)
        {
            Error("Список пуст. Скопируйте имена файлов (Inventor: «Ненайденные файлы» → «Копировать имена») и запустите снова.");
            return 1;
        }
        Ok($"В списке файлов: {names.Count}" + (options.List is null ? " (из буфера обмена)" : $" (из {options.List})"));
        foreach (var name in names.Take(10))
            Console.WriteLine($"  {name}");
        if (names.Count > 10)
            Console.WriteLine($"  … и ещё {names.Count - 10}");

        // 2. Папки
        var source = options.Source ?? PickFolder("Папка с компонентами — где искать (с подпапками)");
        if (source is null || !Directory.Exists(source))
        {
            Error("Не выбрана папка для поиска.");
            return 1;
        }
        Console.WriteLine($"Ищу в: {source}");
        var collector = new Collector(source);
        Console.WriteLine($"Файлов в папке: {collector.FileCount}");

        // 3. Apprentice
        string? why = null;
        using var apprentice = options.NoDependencies ? null : Apprentice.TryStart(out why);
        if (!options.NoDependencies)
        {
            if (apprentice is null)
                Error($"Apprentice не запустился — зависимости не будут собраны ({why}).");
            else
                Ok("Apprentice запущен — читаю зависимости сборок.");
        }

        // 4. Поиск с зависимостями
        Console.WriteLine();
        var result = collector.Collect(names, apprentice, Console.WriteLine);

        Console.WriteLine();
        Title("Итог");
        Ok($"Найдено файлов: {result.Found.Count} (из списка: {result.Found.Count(f => f.Reason is null)}, зависимостей: {result.Found.Count(f => f.Reason is not null)})");
        if (result.Missing.Count > 0)
        {
            Error($"Не найдено: {result.Missing.Count}");
            foreach (var missing in result.Missing)
                Console.WriteLine($"  {missing}");
        }
        foreach (var warning in result.Warnings)
            Warn(warning);

        if (result.Found.Count == 0)
            return 1;

        // 5. Копирование — только после подтверждения
        var target = options.Target ?? PickFolder("Папка, куда скопировать найденные файлы");
        if (target is null)
        {
            Error("Не выбрана папка для результата — ничего не скопировано.");
            return 1;
        }
        Console.WriteLine();
        Console.Write($"Скопировать {result.Found.Count} файлов в «{target}»? (Д/Н): ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (answer is not ("д" or "да" or "y" or "yes" or "l"))
        {
            Warn("Отменено — ничего не скопировано.");
            return 0;
        }

        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        var report = collector.Copy(result, target);
        File.Copy(LogPath, Path.Combine(target, "_журнал.txt"), overwrite: true);
        Ok($"Скопировано: {result.Found.Count}. Отчёт: {report}");
        return 0;
    }

    private static IEnumerable<string> ReadClipboard()
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        return text.Split(["\r\n", "\n"], StringSplitOptions.None);
    }

    private static string? PickFolder(string title)
    {
        Console.WriteLine($"{title}…");
        using var dialog = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true, ShowNewFolderButton = true };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
    }

    private static void Title(string text) => Write(text, ConsoleColor.Cyan);
    private static void Ok(string text) => Write(text, ConsoleColor.Green);
    private static void Warn(string text) => Write(text, ConsoleColor.Yellow);
    private static void Error(string text) => Write(text, ConsoleColor.Red);

    private static void Write(string text, ConsoleColor color)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }
}

/// <summary>Параметры командной строки (все необязательные: без них — диалоги).</summary>
internal sealed record Options(string? List, string? Source, string? Target, bool NoDependencies)
{
    public static Options Parse(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        return new Options(Value("--list"), Value("--source"), Value("--target"),
            args.Any(a => a.Equals("--no-deps", StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>
/// Apprentice Server через позднее связывание (без сборок Inventor): открыть документ и прочитать его ссылки.
/// </summary>
internal sealed class Apprentice : IDisposable, IReferenceReader
{
    private dynamic? _server;

    private Apprentice(object server) => _server = server;

    public static Apprentice? TryStart(out string? reason)
    {
        reason = null;
        try
        {
            var type = Type.GetTypeFromProgID("Inventor.ApprenticeServerComponent");
            if (type is null)
            {
                reason = "не зарегистрирован Inventor.ApprenticeServerComponent — нужен Inventor или Inventor View";
                return null;
            }
            return new Apprentice(Activator.CreateInstance(type)!);
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Ссылки документа. У потерянной ссылки полного пути может не быть — имя берётся из относительного
    /// или логического имени: поиск всё равно по имени.
    /// </summary>
    public IReadOnlyList<DocumentReference> Read(string path)
    {
        var result = new List<DocumentReference>();
        dynamic? document = null;
        try
        {
            document = _server!.Open(path);
            Console.WriteLine($"    [Apprentice] открыт: тип {Get(() => Convert.ToString(document.DocumentType))}");

            // Способ 1: ссылки файла (есть и у потерянных ссылок).
            int count = 0;
            try
            {
                dynamic descriptors = document.File.ReferencedFileDescriptors;
                count = descriptors.Count;
                Console.WriteLine($"    [Apprentice] File.ReferencedFileDescriptors: {count}");
                foreach (var item in Items(descriptors, count))
                {
                    dynamic descriptor = item.Value;
                    var i = item.Index;
                    var full = Get(() => (string?)descriptor.FullFileName);
                    var relative = Get(() => (string?)descriptor.RelativeFileName);
                    var logical = Get(() => (string?)descriptor.LogicalFileName);
                    var missing = Get(() => Convert.ToString(descriptor.ReferenceMissing));
                    Console.WriteLine($"      #{i}: полный «{full}», относительный «{relative}», логический «{logical}», потерян: {missing}");
                    Add(result, full, relative, logical);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [Apprentice] File.ReferencedFileDescriptors — ошибка: {ex.GetType().Name}: {ex.Message}");
            }

            // Способ 2 (если первый ничего не дал): найденные документы, на которые ссылается этот.
            if (result.Count == 0)
            {
                try
                {
                    dynamic documents = document.ReferencedDocuments;
                    int found = documents.Count;
                    Console.WriteLine($"    [Apprentice] ReferencedDocuments: {found}");
                    foreach (var item in Items(documents, found))
                    {
                        dynamic referenced = item.Value;
                        var i = item.Index;
                        var full = Get(() => (string?)referenced.FullFileName);
                        Console.WriteLine($"      документ #{i}: «{full}»");
                        Add(result, full, null, null);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    [Apprentice] ReferencedDocuments — ошибка: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            if (document is not null)
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
        return result;
    }

    /// <summary>
    /// Элементы коллекции Inventor: по номеру (индексатор), через Item(i) или перебором — что сработает.
    /// Каждый неудавшийся способ пишется в журнал.
    /// </summary>
    private static List<(int Index, object Value)> Items(dynamic collection, int count)
    {
        var result = new List<(int, object)>();
        foreach (var (name, read) in new (string, Func<int, object>)[]
                 {
                     ("индексатор", i => collection[i]),
                     ("Item(i)", i => collection.Item(i)),
                 })
        {
            try
            {
                result.Clear();
                for (var i = 1; i <= count; i++)
                    result.Add((i, read(i)));
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [Apprentice] перебор «{name}» не сработал: {ex.GetType().Name}: {ex.Message}");
            }
        }

        try
        {
            result.Clear();
            var index = 0;
            foreach (var value in (System.Collections.IEnumerable)collection)
                result.Add((++index, value));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    [Apprentice] перебор «foreach» не сработал: {ex.GetType().Name}: {ex.Message}");
        }
        return result;
    }

    /// <summary>Имя ссылки — из первого непустого поля (у потерянной ссылки полного пути может не быть).</summary>
    private static void Add(List<DocumentReference> result, string? full, string? relative, string? logical)
    {
        var name = new[] { full, relative, logical }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => Path.GetFileName(v!.TrimEnd('\\', '/')))
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (name is not null && !result.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            result.Add(new DocumentReference(name, string.IsNullOrWhiteSpace(full) ? null : full));
    }

    private static string? Get(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_server is not null)
        {
            Marshal.ReleaseComObject(_server);
            _server = null;
        }
    }
}

/// <summary>Вывод сразу в окно и в файл журнала.</summary>
internal sealed class TeeWriter(TextWriter console, TextWriter file) : TextWriter
{
    public override Encoding Encoding => console.Encoding;
    public override void Write(char value) { console.Write(value); file.Write(value); }
    public override void Write(string? value) { console.Write(value); file.Write(value); }
    public override void WriteLine(string? value) { console.WriteLine(value); file.WriteLine(value); }
}
