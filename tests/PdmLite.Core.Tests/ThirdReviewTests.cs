using System.IO.Pipes;
using System.Text.Json;
using CollectComponents;
using PdmLite.Core.Documents;
using PdmLite.Core.Naming;
using PdmLite.Core.Scripts;

namespace PdmLite.Core.Tests;

/// <summary>Третий проход: естественный порядок — граничные случаи.</summary>
public class NaturalComparerTheoryTests
{
    [Theory]
    [InlineData("Болт M6", "Болт M8", -1)]
    [InlineData("Болт M8", "Болт M10", -1)]
    [InlineData("Болт M10", "Болт M8", 1)]
    [InlineData("Болт M10x20", "Болт M10x100", -1)]
    [InlineData("Шайба 1,6", "Шайба 16", -1)]
    [InlineData("Шайба 1.6", "Шайба 1.10", -1)] // «.» — не десятичная точка, части сравниваются как числа
    [InlineData("a", "A", 1)] // равны без учёта регистра — порядок устойчивый, по коду символа
    [InlineData("A", "a", -1)]
    [InlineData("abc", "abd", -1)]
    [InlineData("ab", "abc", -1)]
    [InlineData("1", "a", -1)] // цифры раньше букв
    [InlineData("0", "00", -1)]
    [InlineData("0", "1", -1)]
    [InlineData("000", "0", 1)]
    [InlineData("1a", "1b", -1)]
    [InlineData("2a", "10a", -1)]
    [InlineData("ГОСТ 7798-70", "ГОСТ 7805-70", -1)]
    [InlineData("ГОСТ 2.101", "ГОСТ 2.11", 1)]
    [InlineData("Ж", "е", 1)]
    [InlineData("x 1", "x  1", -1)] // разное число пробелов — разные строки, сравнение не падает
    [InlineData("18446744073709551616", "18446744073709551615", 1)] // больше ulong
    [InlineData("", "", 0)]
    public void Compare(string x, string y, int expected) =>
        Assert.Equal(expected, Math.Sign(NaturalComparer.Instance.Compare(x, y)));

    [Theory]
    [InlineData("a10", "a10")]
    [InlineData("Болт M8", "Болт M8")]
    public void Equal_IsZero_AndSymmetric(string x, string y)
    {
        Assert.Equal(0, NaturalComparer.Instance.Compare(x, y));
        Assert.Equal(0, NaturalComparer.Instance.Compare(y, x));
    }

    [Fact]
    public void NonGenericComparer_HandlesObjectsAndNulls()
    {
        System.Collections.IComparer comparer = NaturalComparer.Instance;
        Assert.True(comparer.Compare(2, 10) < 0); // числа — по ToString
        Assert.True(comparer.Compare(null, "a") < 0);
        Assert.True(comparer.Compare("a", null) > 0);
        Assert.Equal(0, comparer.Compare(null, null));
    }

    [Fact]
    public void Antisymmetric_OnSample()
    {
        string[] sample = ["", "0", "00", "1", "01", "a", "A", "a1", "a01", "a1b", "a10", "Болт M8", "болт M08", "x99", "x100"];
        foreach (var x in sample)
        foreach (var y in sample)
            Assert.Equal(-Math.Sign(NaturalComparer.Instance.Compare(y, x)), Math.Sign(NaturalComparer.Instance.Compare(x, y)));
    }

    [Fact]
    public void Transitive_OnSample()
    {
        string[] sample = ["0", "00", "1", "01", "a", "A", "a1", "a01", "a1b", "a10", "b", "B2", "b10"];
        var c = NaturalComparer.Instance;
        foreach (var x in sample)
        foreach (var y in sample)
        foreach (var z in sample)
            if (c.Compare(x, y) <= 0 && c.Compare(y, z) <= 0)
                Assert.True(c.Compare(x, z) <= 0, $"{x} ≤ {y} ≤ {z}");
    }
}

/// <summary>Третий проход: имена вхождений.</summary>
public class OccurrenceNamingTheoryTests
{
    [Theory]
    [InlineData("Болт:1", "Болт", true)]
    [InlineData("Болт:0", "Болт", true)]
    [InlineData("Болт:999", "Болт", true)]
    [InlineData("Болт:", "Болт", false)]
    [InlineData("Болт :1", "Болт", false)]
    [InlineData("болт:1", "Болт", false)] // регистр имени важен для обозревателя
    [InlineData("Болт:1:2", "Болт", false)]
    [InlineData("Болт (1.5):1", "Болт (1.5)", true)] // символы регулярных выражений в имени
    [InlineData("Болт (125):1", "Болт (1.5)", false)]
    [InlineData("a+b:1", "a+b", true)]
    [InlineData("aab:1", "a+b", false)]
    [InlineData("Болт:-1", "Болт", false)]
    public void IsDefault(string name, string baseName, bool expected) =>
        Assert.Equal(expected, OccurrenceNaming.IsDefault(name, baseName));

    [Fact]
    public void Plan_TwoNewNames_GetConsecutiveFreeNumbers()
    {
        var plan = OccurrenceNaming.Plan(["Болт:2", "Старый:1", "Старый:2"], [("Старый:1", "Болт"), ("Старый:2", "Болт")]);
        Assert.Equal([("Старый:1", "Болт:1"), ("Старый:2", "Болт:3")], plan);
    }

    [Fact]
    public void Plan_NameBeingRenamed_FreesItsNumber()
    {
        // «Болт:1» — имя кандидата под другой документ: освобождается и может достаться другому.
        var plan = OccurrenceNaming.Plan(["Болт:1"], [("Болт:1", "Гайка")]);
        Assert.Equal([("Болт:1", "Гайка:1")], plan);
    }

    [Fact]
    public void Plan_NoCandidates_Empty() => Assert.Empty(OccurrenceNaming.Plan(["a:1"], []));

    [Fact]
    public void Plan_WhitespaceBaseName_Skipped() => Assert.Empty(OccurrenceNaming.Plan(["x"], [("x", "   ")]));

    [Fact]
    public void Plan_CaseInsensitiveUsedNames()
    {
        var plan = OccurrenceNaming.Plan(["БОЛТ:1", "x"], [("x", "Болт")]);
        Assert.Equal([("x", "Болт:2")], plan);
    }
}

/// <summary>Третий проход: имя файла по правилу.</summary>
public class FileNameBuilderTheoryTests
{
    [Theory]
    [InlineData("АБВГ.301.001", "Рама", null, "", "АБВГ.301.001 Рама")]
    [InlineData("АБВГ.301.001", "Рама", "", ".iam", "АБВГ.301.001 Рама.iam")]
    [InlineData("АБВГ.301.001", "Рама", null, " .iam ", "АБВГ.301.001 Рама.iam")]
    [InlineData("АБВГ.301.001 ", "Рама", "СБ ", "idw", "АБВГ.301.001СБ Рама.idw")]
    [InlineData("АБВГ/301", "Рама", null, ".ipt", "АБВГ-301 Рама.ipt")]
    [InlineData("АБВГ.301.001", "***", null, ".ipt", "АБВГ.301.001.ipt")]
    [InlineData("АБВГ.301.001", "Рама\tсварная", null, ".ipt", "АБВГ.301.001 Рама сварная.ipt")]
    [InlineData("АБВГ.301.001", "Рама\nсварная", null, ".ipt", "АБВГ.301.001 Рама сварная.ipt")]
    public void Build(string designation, string name, string? code, string ext, string expected) =>
        Assert.Equal(expected, FileNameBuilder.Build(designation, name, code, ext));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("***")]
    [InlineData("...")]
    [InlineData("- -")]
    public void Build_NoDesignation_Throws_EvenWithDocumentCode(string designation) =>
        Assert.Throws<ArgumentException>(() => FileNameBuilder.Build(designation, "Рама", "СБ", ".idw"));

    [Theory]
    [InlineData("", "")]
    [InlineData("a<b>c", "abc")]
    [InlineData("a|b", "ab")]
    [InlineData("a\u0001b", "ab")]
    [InlineData("a - b", "a - b")] // пробелы в наименовании сохраняются
    [InlineData("a--b", "a-b")] // повторы дефиса схлопываются
    [InlineData("..a..", "a")]
    [InlineData("a\\\\b", "a-b")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, FileNameBuilder.Normalize(input));
}

/// <summary>Третий проход: обмен с процессом свойств — сбои канала и данных.</summary>
public class PropertyHostEdgeTests
{
    private sealed class Store : IDocumentPropertyStore
    {
        public List<string> Calls { get; } = [];
        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public Task<DocumentProperties> ReadAsync(string path, CancellationToken ct = default)
        {
            Calls.Add("read " + path);
            return Task.FromResult(new DocumentProperties { PartNumber = path });
        }

        public Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken ct = default)
        {
            Calls.Add("write " + path + " " + changes.PartNumber);
            return Task.CompletedTask;
        }

        public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string, string> map, CancellationToken ct = default)
        {
            Calls.Add($"replace {path} {map.Count} {map.ContainsKey("A.IPT")}");
            return Task.CompletedTask;
        }
    }

    private static async Task<(List<PropertyHostResponse> Responses, Store Store)> Serve(params string[] lines)
    {
        var store = new Store();
        var output = new StringWriter();
        await PropertyHostProtocol.ServeAsync(new StringReader(string.Join("\n", lines)), output, store);
        var responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonSerializer.Deserialize<PropertyHostResponse>(l, PropertyHostProtocol.Json)!).ToList();
        return (responses, store);
    }

    private static string Request(PropertyHostRequest request) => JsonSerializer.Serialize(request, PropertyHostProtocol.Json);

    [Fact]
    public async Task Serve_EmptyLines_Skipped()
    {
        var (responses, _) = await Serve("", Request(new("read", "a")), "", "");
        Assert.True(Assert.Single(responses).Ok);
    }

    [Theory]
    [InlineData("не json")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("123")]
    public async Task Serve_BrokenRequest_AnswersError_AndContinues(string broken)
    {
        var (responses, store) = await Serve(broken, Request(new("read", "после")));
        Assert.Equal(2, responses.Count);
        Assert.False(responses[0].Ok);
        Assert.False(string.IsNullOrEmpty(responses[0].Error));
        Assert.True(responses[1].Ok);
        Assert.Equal(["read после"], store.Calls);
    }

    [Fact]
    public async Task Serve_ReadNoGeometry_FallsBackToDefaultInterfaceMethod()
    {
        var (responses, store) = await Serve(Request(new("readNoGeometry", "x")));
        Assert.Equal("x", responses[0].Properties!.PartNumber);
        Assert.Equal(["read x"], store.Calls); // хранилище без отдельного чтения — обычное чтение
    }

    [Fact]
    public async Task Serve_WriteWithoutChanges_And_ReplaceWithoutMap_AreAccepted()
    {
        var (responses, store) = await Serve("""{"Op":"write","Path":"a"}""", """{"Op":"replace","Path":"b"}""");
        Assert.All(responses, r => Assert.True(r.Ok));
        Assert.Equal(["write a ", "replace b 0 False"], store.Calls);
    }

    [Fact]
    public async Task Serve_ReplaceMap_IsCaseInsensitive()
    {
        var (_, store) = await Serve(Request(new("replace", "s.iam", Map: new() { ["a.ipt"] = "b.ipt" })));
        Assert.Equal(["replace s.iam 1 True"], store.Calls);
    }

    [Fact]
    public async Task Serve_Cyrillic_NotEscaped()
    {
        var output = new StringWriter();
        await PropertyHostProtocol.ServeAsync(new StringReader(Request(new("read", "Болт"))), output, new Store());
        Assert.Contains("Болт", output.ToString());
    }

    [Fact]
    public async Task Client_ServerClosed_IOException()
    {
        var client = new PropertyHostClient(new StringReader(""), new StringWriter());
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync(new("read", "a"), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Client_ErrorResponse_WithoutText_HasDefaultMessage()
    {
        var client = new PropertyHostClient(new StringReader("""{"Ok":false}""" + "\n"), new StringWriter());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new("read", "a"), TimeSpan.FromSeconds(5)));
        Assert.Contains("Ошибка процесса свойств", error.Message);
        Assert.False(client.IsBroken); // ответ получен — порядок не сбит
    }

    [Fact]
    public async Task Client_NullResponse_InvalidData()
    {
        var client = new PropertyHostClient(new StringReader("null\n"), new StringWriter());
        await Assert.ThrowsAsync<InvalidDataException>(() => client.SendAsync(new("read", "a"), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Client_WritesOneLinePerRequest()
    {
        var output = new StringWriter();
        var client = new PropertyHostClient(new StringReader("{\"Ok\":true}\n{\"Ok\":true}\n"), output);
        await client.SendAsync(new("read", "a\nb"), TimeSpan.FromSeconds(5)); // перевод строки в пути не рвёт протокол
        await client.SendAsync(new("read", "c"), TimeSpan.FromSeconds(5));
        Assert.Equal(2, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private static (PropertyHostClient Client, StreamWriter Responses) SilentServer()
    {
        var responses = new AnonymousPipeServerStream(PipeDirection.Out);
        var responsesIn = new AnonymousPipeClientStream(PipeDirection.In, responses.ClientSafePipeHandle);
        return (new PropertyHostClient(new StreamReader(responsesIn), new StringWriter()), new StreamWriter(responses) { AutoFlush = true });
    }

    [Fact]
    public async Task Client_Timeout_TimeoutException_ThenBroken()
    {
        var (client, responses) = SilentServer();
        await Assert.ThrowsAsync<TimeoutException>(() => client.SendAsync(new("read", "a"), TimeSpan.FromMilliseconds(100)));
        Assert.True(client.IsBroken);

        // Опоздавший ответ на первый запрос не должен стать ответом на второй.
        await responses.WriteLineAsync("""{"Ok":true,"Properties":{"PartNumber":"a"}}""");
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync(new("read", "b"), TimeSpan.FromSeconds(5)));
        responses.Dispose();
    }

    [Fact]
    public async Task Client_Cancelled_OperationCanceled_ThenBroken()
    {
        var (client, responses) = SilentServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(new("read", "a"), TimeSpan.FromMinutes(1), cts.Token));
        Assert.True(client.IsBroken);
        responses.Dispose();
    }

    [Fact]
    public async Task Client_CancelledBeforeSend_NotBroken()
    {
        var client = new PropertyHostClient(new StringReader(""), new StringWriter());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(new("read", "a"), TimeSpan.FromMinutes(1), cts.Token));
        Assert.False(client.IsBroken); // запрос не ушёл — порядок цел
    }

    [Fact]
    public async Task UnavailableStore_Throws_WithReason()
    {
        var store = new UnavailablePropertyStore("нет Inventor");
        Assert.False(store.IsAvailable);
        Assert.Equal("нет Inventor", store.UnavailableReason);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => store.ReadAsync("a"));
        Assert.Contains("нет Inventor", error.Message);
    }
}

/// <summary>Третий проход: каталог внешних утилит.</summary>
public sealed class ScriptCatalogEdgeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdmlite-scripts3-").FullName;

    private string Add(string relative, string content = "x")
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string Folder => Path.Combine(_root, "Scripts");
    private string Bundled => Path.Combine(_root, "bundled");

    [Theory]
    [InlineData("""{"Title":42}""")]
    [InlineData("""{"Title":["a"]}""")]
    [InlineData("""["Title"]""")]
    [InlineData("""{"Title":null}""")]
    [InlineData("""{"Title":"   "}""")]
    [InlineData("")]
    [InlineData("\"строка\"")]
    public void List_BadManifest_FallsBackToFileName(string manifest)
    {
        Add("Scripts/Отчёт.cmd");
        Add("Scripts/Отчёт.json", manifest);
        var script = Assert.Single(ScriptCatalog.List(Folder));
        Assert.Equal("Отчёт", script.Title);
    }

    [Fact]
    public void List_DescriptionNotString_Ignored()
    {
        Add("Scripts/a.cmd");
        Add("Scripts/a.json", """{"Title":"А","Description":{"x":1}}""");
        var script = Assert.Single(ScriptCatalog.List(Folder));
        Assert.Equal("А", script.Title);
        Assert.Null(script.Description);
    }

    [Fact]
    public void List_BatHidesPs1_ButExeAndPs1BothShown()
    {
        Add("Scripts/a.bat");
        Add("Scripts/a.ps1");
        Add("Scripts/b.exe");
        Add("Scripts/b.ps1");
        Assert.Equal(["a.bat", "b.exe", "b.ps1"], ScriptCatalog.List(Folder).Select(s => s.FileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void List_LauncherInOtherFolder_DoesNotHidePs1()
    {
        Add("Scripts/one/a.cmd");
        Add("Scripts/two/a.ps1");
        Assert.Equal(2, ScriptCatalog.List(Folder).Count);
    }

    [Fact]
    public void List_UppercaseExtensions_Recognized()
    {
        Add("Scripts/A.CMD");
        Add("Scripts/B.PS1");
        Add("Scripts/C.TXT");
        Assert.Equal(["A", "B"], ScriptCatalog.List(Folder).Select(s => s.Title));
    }

    [Fact]
    public void List_EmptyFolder_Empty()
    {
        Directory.CreateDirectory(Folder);
        Assert.Empty(ScriptCatalog.List(Folder));
    }

    [Fact]
    public void Seed_NoBundledFolder_DoesNothing()
    {
        ScriptCatalog.Seed(Folder, Path.Combine(_root, "нет"));
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public void Seed_Twice_Idempotent()
    {
        Add("bundled/a.cmd", "1");
        ScriptCatalog.Seed(Folder, Bundled);
        ScriptCatalog.Seed(Folder, Bundled);
        Assert.Equal("1", File.ReadAllText(Path.Combine(Folder, "a.cmd")));
    }

    [Fact]
    public void Seed_NoLongerShipped_Unchanged_Removed_Changed_Kept()
    {
        Add("bundled/old.cmd", "старая");
        Add("bundled/mine.cmd", "моя");
        ScriptCatalog.Seed(Folder, Bundled);
        File.WriteAllText(Path.Combine(Folder, "mine.cmd"), "правка");
        File.Delete(Path.Combine(Bundled, "old.cmd"));
        File.Delete(Path.Combine(Bundled, "mine.cmd"));

        ScriptCatalog.Seed(Folder, Bundled);

        Assert.False(File.Exists(Path.Combine(Folder, "old.cmd")));
        Assert.Equal("правка", File.ReadAllText(Path.Combine(Folder, "mine.cmd")));
    }

    [Fact]
    public void Seed_UserFileWithSameName_NotInManifest_NotOverwritten()
    {
        Add("Scripts/.bundled.json", "{}"); // учёт уже есть (не первая версия)
        Add("Scripts/a.cmd", "своя утилита пользователя");
        Add("bundled/a.cmd", "поставляемая");

        ScriptCatalog.Seed(Folder, Bundled);

        Assert.Equal("своя утилита пользователя", File.ReadAllText(Path.Combine(Folder, "a.cmd")));
    }

    [Fact]
    public void Seed_CorruptManifest_TreatedAsEmpty()
    {
        Add("Scripts/.bundled.json", "{испорчен");
        Add("bundled/a.cmd", "1");
        ScriptCatalog.Seed(Folder, Bundled);
        Assert.Equal("1", File.ReadAllText(Path.Combine(Folder, "a.cmd")));
    }

    [Fact]
    public void Seed_ReadOnlyInstalledCopy_StillUpdated()
    {
        Add("bundled/a.cmd", "1");
        ScriptCatalog.Seed(Folder, Bundled);
        var target = Path.Combine(Folder, "a.cmd");
        File.SetAttributes(target, FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(Bundled, "a.cmd"), "2");

        ScriptCatalog.Seed(Folder, Bundled);

        Assert.Equal("2", File.ReadAllText(target));
    }

    [Fact]
    public void Seed_ManifestNamedFileInBundle_Ignored()
    {
        Add("bundled/.bundled.json", """{"чужое":"x"}""");
        Add("bundled/a.cmd", "1");
        ScriptCatalog.Seed(Folder, Bundled);
        ScriptCatalog.Seed(Folder, Bundled);
        Assert.DoesNotContain("чужое", File.ReadAllText(Path.Combine(Folder, ".bundled.json")));
        Assert.True(File.Exists(Path.Combine(Folder, "a.cmd")));
    }

    [Fact]
    public void Seed_NestedFolders_Created()
    {
        Add("bundled/x/y/z.cmd", "1");
        ScriptCatalog.Seed(Folder, Bundled);
        Assert.True(File.Exists(Path.Combine(Folder, "x", "y", "z.cmd")));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}

/// <summary>Третий проход: утилита сбора — циклы, повторы, сбои чтения, повторный запуск.</summary>
public sealed class CollectComponentsEdgeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdmlite-collect3-").FullName;
    private string Source => Path.Combine(_root, "src");
    private string Target => Path.Combine(_root, "out");

    private string Add(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(Source, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, relative);
        return path;
    }

    private sealed class Refs(Dictionary<string, string[]> map, string? failOn = null) : IReferenceReader
    {
        public List<string> Reads { get; } = [];

        public IReadOnlyList<DocumentReference> Read(string path)
        {
            var name = Path.GetFileName(path);
            Reads.Add(name);
            if (name == failOn)
                throw new InvalidOperationException("файл повреждён");
            return map.TryGetValue(name, out var refs) ? refs.Select(r => new DocumentReference(r, null)).ToList() : [];
        }
    }

    private static List<string> Names(CollectResult result) => result.Found.Select(f => Path.GetFileName(f.Path)).ToList();

    [Theory]
    [InlineData("Блок.iam\r", "Блок.iam")]
    [InlineData("D:/Папка/Блок.iam", "Блок.iam")]
    [InlineData("  \"D:\\Папка\\Блок.iam\"  ", "Блок.iam")]
    [InlineData("Блок.iam\t\tлишнее", "Блок.iam")]
    [InlineData("\\\\server\\share\\Блок.iam", "Блок.iam")]
    [InlineData("Имя с пробелами.ipt", "Имя с пробелами.ipt")]
    [InlineData("Без расширения", "Без расширения")]
    public void ParseNames_Single(string line, string expected) => Assert.Equal([expected], Collector.ParseNames([line]));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\"\"")]
    [InlineData("D:\\Папка\\")]
    public void ParseNames_NothingUseful(string line) => Assert.Empty(Collector.ParseNames([line]));

    [Theory]
    [InlineData("a.ipt", true)]
    [InlineData("a.IAM", true)]
    [InlineData("a.idw", true)]
    [InlineData("a.dwg", true)]
    [InlineData("a.ipn", true)]
    [InlineData("a.step", false)]
    [InlineData("a.pdf", false)]
    [InlineData("a", false)]
    public void IsInventorFile(string path, bool expected) => Assert.Equal(expected, Collector.IsInventorFile(path));

    [Fact]
    public void Cycle_AB_Terminates_EachOnce()
    {
        Add("A.iam");
        Add("B.iam");
        var refs = new Refs(new() { ["A.iam"] = ["B.iam"], ["B.iam"] = ["A.iam"] });
        var result = new Collector(Source).Collect(["A.iam"], refs, _ => { });
        Assert.Equal(["A.iam", "B.iam"], Names(result));
        Assert.Empty(result.Missing);
    }

    [Fact]
    public void SelfReference_Ignored()
    {
        Add("A.iam");
        var result = new Collector(Source).Collect(["A.iam"], new Refs(new() { ["A.iam"] = ["A.iam"] }), _ => { });
        Assert.Equal(["A.iam"], Names(result));
    }

    [Fact]
    public void ReaderThrows_WarningAndFileStillCopied_OthersContinue()
    {
        Add("A.iam");
        Add("B.iam");
        Add("C.ipt");
        var refs = new Refs(new() { ["B.iam"] = ["C.ipt"] }, failOn: "A.iam");
        var log = new List<string>();

        var result = new Collector(Source).Collect(["A.iam", "B.iam"], refs, log.Add);

        Assert.Equal(["A.iam", "B.iam", "C.ipt"], Names(result));
        Assert.Contains(result.Warnings, w => w.Contains("A.iam") && w.Contains("файл повреждён"));
        Assert.Contains(log, l => l.Contains("НЕ прочитаны"));
    }

    [Fact]
    public void NonInventorFile_NotReadForReferences()
    {
        Add("Схема.pdf");
        var refs = new Refs([]);
        var result = new Collector(Source).Collect(["Схема.pdf"], refs, _ => { });
        Assert.Equal(["Схема.pdf"], Names(result));
        Assert.Empty(refs.Reads);
    }

    [Fact]
    public void ListedTwice_DifferentCase_CopiedOnce()
    {
        Add("A.iam");
        var result = new Collector(Source).Collect(["A.iam", "a.IAM"], null, _ => { });
        Assert.Single(result.Found);
    }

    [Fact]
    public void DependencyAlsoListed_StaysOnTop_NotInSubfolder()
    {
        Add("A.iam");
        Add("B.ipt");
        var result = new Collector(Source).Collect(["A.iam", "B.ipt"], new Refs(new() { ["A.iam"] = ["B.ipt"] }), _ => { });
        Assert.All(result.Found, f => Assert.Null(f.Root));
    }

    [Fact]
    public void ListedComponentMissing_DependencyOfAnother_StillFoundBelow()
    {
        Add("A.iam");
        Add("B.ipt");
        var result = new Collector(Source).Collect(["Нет.iam", "A.iam"], new Refs(new() { ["A.iam"] = ["B.ipt"] }), _ => { });
        Assert.Equal(["A.iam", "B.ipt"], Names(result));
        Assert.Equal(["Нет.iam (из списка)"], result.Missing);
    }

    [Fact]
    public void MissingDependency_ReportedOnce_EvenIfReferencedTwice()
    {
        Add("A.iam");
        Add("B.iam");
        var refs = new Refs(new() { ["A.iam"] = ["Пропал.ipt"], ["B.iam"] = ["Пропал.ipt"] });
        var result = new Collector(Source).Collect(["A.iam", "B.iam"], refs, _ => { });
        Assert.Single(result.Missing);
    }

    [Fact]
    public void OldVersions_AnyCase_Skipped()
    {
        Add("oldversions/A.iam");
        Add("OLDVERSIONS/B.iam");
        Add("Папка/OldVersions/C.iam");
        var collector = new Collector(Source);
        Assert.Equal(0, collector.FileCount);
    }

    [Fact]
    public void FolderNamedLikeOldVersionsPrefix_NotSkipped()
    {
        Add("OldVersionsArchive/A.iam");
        Assert.Equal(1, new Collector(Source).FileCount);
    }

    [Fact]
    public void SourceWithTrailingSeparator_Normalized()
    {
        Add("A.iam");
        var collector = new Collector(Source + Path.DirectorySeparatorChar);
        var result = collector.Collect(["A.iam"], null, _ => { });
        var report = File.ReadAllText(collector.Copy(result, Target));
        Assert.Contains("Где искали: " + Source + Environment.NewLine, report);
    }

    [Fact]
    public void SourceMissing_Throws() =>
        Assert.Throws<DirectoryNotFoundException>(() => new Collector(Path.Combine(_root, "нет")));

    [Fact]
    public void DeepChain_AllInFolderOfTop()
    {
        Add("A.iam");
        Add("B.iam");
        Add("C.iam");
        Add("D.ipt");
        var refs = new Refs(new() { ["A.iam"] = ["B.iam"], ["B.iam"] = ["C.iam"], ["C.iam"] = ["D.ipt"] });
        var result = new Collector(Source).Collect(["A.iam"], refs, _ => { });
        Assert.Equal([null, "A.iam", "A.iam", "A.iam"], result.Found.Select(f => f.Root));
        Assert.Equal([null, "A.iam", "B.iam", "C.iam"], result.Found.Select(f => f.Reason));
    }

    [Fact]
    public void RelativeTarget_TopAndComposition()
    {
        Assert.Equal("A.iam", new FoundFile(Path.Combine("x", "A.iam"), null).RelativeTarget);
        Assert.Equal(Path.Combine("A", "B.ipt"), new FoundFile(Path.Combine("x", "B.ipt"), "A.iam", "A.iam").RelativeTarget);
    }

    [Fact]
    public void Copy_Rerun_OverwritesReadOnlyCopyFromPreviousRun()
    {
        Add("A.iam");
        var collector = new Collector(Source);
        var result = collector.Collect(["A.iam"], null, _ => { });
        collector.Copy(result, Target);
        var copy = Path.Combine(Target, "A.iam");
        File.SetAttributes(copy, FileAttributes.ReadOnly); // например, вернули из архива

        collector.Copy(result, Target);

        Assert.True(File.Exists(copy));
        Assert.False(File.GetAttributes(copy).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void Copy_TargetIsSourceFolder_DoesNotFailOnSameFile()
    {
        var a = Add("A.iam");
        var collector = new Collector(Source);
        var result = collector.Collect(["A.iam"], null, _ => { });

        collector.Copy(result, Source);

        Assert.Equal("A.iam", File.ReadAllText(a));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("sub", true)]
    [InlineData("sub/deeper", true)]
    [InlineData("..", false)]
    [InlineData("../out", false)]
    [InlineData("../src2", false)] // общее начало имени — ещё не «внутри»
    public void IsInsideSource(string relative, bool expected)
    {
        Directory.CreateDirectory(Source);
        Assert.Equal(expected, new Collector(Source).IsInsideSource(Path.Combine(Source, relative)));
    }

    [Fact]
    public void Copy_ReportListsWarnings_AndDependencyReasons()
    {
        Add("A.iam");
        Add("B.ipt");
        Add("x/C.ipt");
        Add("y/C.ipt");
        var collector = new Collector(Source);
        var result = collector.Collect(["A.iam", "C.ipt"], new Refs(new() { ["A.iam"] = ["B.ipt"] }), _ => { });

        var report = File.ReadAllText(collector.Copy(result, Target));

        Assert.Contains("СКОПИРОВАНО (3)", report);
        Assert.Contains("[зависимость A.iam]", report);
        Assert.Contains("ЗАМЕЧАНИЯ (1)", report);
        Assert.Contains("НЕ НАЙДЕНО (0)", report);
    }

    [Fact]
    public void Copy_NoWarnings_NoWarningsSection()
    {
        Add("A.iam");
        var collector = new Collector(Source);
        var report = File.ReadAllText(collector.Copy(collector.Collect(["A.iam"], null, _ => { }), Target));
        Assert.DoesNotContain("ЗАМЕЧАНИЯ", report);
    }

    [Fact]
    public void Collect_EmptyList_NothingFound()
    {
        Add("A.iam");
        var result = new Collector(Source).Collect([], new Refs([]), _ => { });
        Assert.Empty(result.Found);
        Assert.Empty(result.Missing);
    }

    [Fact]
    public void Collect_SharedSubassembly_ReadOnce_PlacedUnderFirst()
    {
        Add("A.iam");
        Add("B.iam");
        Add("S.iam");
        Add("P.ipt");
        var refs = new Refs(new() { ["A.iam"] = ["S.iam"], ["B.iam"] = ["S.iam"], ["S.iam"] = ["P.ipt"] });
        var result = new Collector(Source).Collect(["A.iam", "B.iam"], refs, _ => { });
        Assert.Equal("A.iam", result.Found.Single(f => f.Path.EndsWith("P.ipt")).Root);
        Assert.Equal(1, refs.Reads.Count(r => r == "S.iam"));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}
