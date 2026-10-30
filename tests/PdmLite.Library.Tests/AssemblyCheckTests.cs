using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Checks;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public class AssemblyCheckTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "pdmlite-check-lib");
    private static readonly LibraryStorage Storage = new(Root);

    private static LibraryItemSummary Item(string designation, string relative) =>
        new(Guid.NewGuid(), designation, LibraryItemKind.Standard, LibraryDocumentType.Part, null, null, 1, "Крепёж/Болты", relative, 1,
            DateTime.UtcNow, "test");

    private static readonly LibraryItemSummary Bolt = Item("Болт M8-6gx20.109 ГОСТ 7798-70", "Крепёж/Болты/ГОСТ 7798-70/Болт M8-6gx20.109 ГОСТ 7798-70.ipt");
    private static readonly LibraryItemSummary Nut = Item("Гайка M8-6H.10 ГОСТ 5915-70", "Крепёж/Гайки/ГОСТ 5915-70/Гайка M8-6H.10 ГОСТ 5915-70.ipt");

    private static AssemblyComponentInfo Part(string path, string? partNumber = null, bool cc = false, int count = 1) =>
        new(path, partNumber, cc, count);

    [Fact]
    public void Run_ClassifiesComponents()
    {
        var components = new[]
        {
            Part(Storage.GetFullPath(Bolt.RelativePath), Bolt.Designation, count: 4),                                // уже из библиотеки
            Part(@"C:\CC\ISO 4017 M8x20.ipt", "Болт М8-6gх20.109 ГОСТ 7798-70", cc: true),                           // Content Center, есть аналог
            Part(@"C:\CC\Шайба 8 ГОСТ 11371-78.ipt", "Шайба A.8.01.08кп.016 ГОСТ 11371-78", cc: true),               // Content Center, аналога нет
            Part(@"C:\Проект\Гайка M8-6H.10 ГОСТ 5915-70.ipt", "Гайка M8-6H.10 ГОСТ 5915-70"),                       // копия библиотечной
            Part(@"C:\Проект\Штифт 3x26 ГОСТ 3128-70.ipt"),                                                        // стандартное, нет в библиотеке
            Part(@"C:\Проект\АБВГ.301.001_Рама.ipt", "АБВГ.301.001"),                                                // своя деталь
        };

        var result = AssemblyCheck.Run(components, [Bolt, Nut], Storage);

        Assert.Equal(6, result.Total);
        Assert.Equal(1, result.InLibrary);
        Assert.Equal(4, result.Findings.Count);

        var ccBolt = result.Findings.Single(f => f.Component.Path.EndsWith("M8x20.ipt"));
        Assert.Equal(ComponentFinding.ContentCenter, ccBolt.Finding);
        Assert.Same(Bolt, ccBolt.Replacement);

        var ccWasher = result.Findings.Single(f => f.Component.Path.Contains("Шайба"));
        Assert.Equal(ComponentFinding.ContentCenter, ccWasher.Finding);
        Assert.False(ccWasher.CanReplace);
        Assert.Contains("нет", ccWasher.Message);

        var copy = result.Findings.Single(f => f.Finding == ComponentFinding.CopyOfLibraryItem);
        Assert.Same(Nut, copy.Replacement);

        var pin = result.Findings.Single(f => f.Finding == ComponentFinding.StandardNotInLibrary);
        Assert.Equal("Штифт 3x26 ГОСТ 3128-70", pin.Designation); // обозначение из имени файла, если свойство пустое
    }

    [Fact]
    public void Run_EmptyAssembly_HasNoFindings()
    {
        var result = AssemblyCheck.Run([], [Bolt], Storage);
        Assert.Equal(0, result.Total);
        Assert.Empty(result.Findings);
    }
}
