using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Domain;
using PdmLite.Core.Projects;

namespace PdmLite.Core.Tests;

public sealed class ProjectScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdmlite-").FullName;

    private void Touch(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    [Fact]
    public async Task Scan_KeepsInventorFilesAndSkipsServiceFolders()
    {
        Touch("Сборки/АБВГ.301.001_Рама.iam");
        Touch("Сборки/АБВГ.301.001СБ_Рама.dwg");
        Touch("Детали/АБВГ.301.002_Ось.ipt");
        Touch("Детали/OldVersions/АБВГ.301.002_Ось.0001.ipt");
        Touch("Документы/readme.txt");

        var root = await new FileSystemProjectScanner(NullLogger<FileSystemProjectScanner>.Instance)
            .ScanAsync(ProjectInfo.FromPath(_root));

        Assert.Equal(["Детали", "Сборки"], root.Children.Select(c => c.Name));
        var files = root.Descendants().Where(i => !i.IsFolder).Select(i => (i.Name, i.Kind)).ToList();
        Assert.Equal(3, files.Count);
        Assert.Contains(("АБВГ.301.001СБ_Рама.dwg", DocumentKind.Drawing), files);
        Assert.DoesNotContain(files, f => f.Name.Contains("0001"));
    }

    [Fact]
    public void ProjectInfo_FromIpj_UsesWorkspaceFolder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Workspace"));
        var ipj = Path.Combine(_root, "Изделие.ipj");
        File.WriteAllText(ipj, "<InventorProject><WorkspacePath>.\\Workspace</WorkspacePath></InventorProject>");

        var project = ProjectInfo.FromPath(ipj);

        Assert.Equal("Изделие", project.Name);
        Assert.Equal(ipj, project.ProjectFile);
        Assert.Equal(Path.Combine(_root, "Workspace"), project.RootFolder);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
