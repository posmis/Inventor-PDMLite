using System.Collections;
using CollectComponents;
using ShortenNames;

namespace PdmLite.Core.Tests;

public class UtilityCompletenessTests
{
    public sealed class Collection(object[] values, int readable = int.MaxValue, bool failIndexer = false) : IEnumerable
    {
        public int Count => values.Length;
        [System.Runtime.CompilerServices.IndexerName("At")]
        public object this[int index] => failIndexer ? throw new IOException("indexer failed") : Read(index);
        public object Item(int index) => Read(index);
        private object Read(int index) => index <= readable ? values[index - 1] : throw new IOException("broken descriptor");
        public IEnumerator GetEnumerator()
        {
            for (var i = 1; i <= Count; i++) yield return this[i];
        }
    }
    public sealed class Descriptor(string? full, string? relative = null)
    {
        public string? FullFileName => full;
        public string? RelativeFileName => relative;
        public string? LogicalFileName => null;
    }
    public sealed class FileData(Collection descriptors) { public Collection ReferencedFileDescriptors => descriptors; }
    public sealed class Document(Collection descriptors, Collection fallback, bool failPrimary = false)
    {
        public FileData File => failPrimary ? throw new IOException("primary failure") : new(descriptors);
        public Collection ReferencedDocuments => fallback;
    }
    private sealed class Reader(object document) : IReferenceReader
    { public IReadOnlyList<DocumentReference> Read(string path) => DocumentReferences.Read(document); }

    [Fact]
    public void EmptyDocument_IsCompleteWhenBothCollectionsAreEmpty()
    {
        Assert.Empty(DocumentReferences.Read(new Document(new([]), new([]))));
    }

    [Fact]
    public void EmptyPrimary_NonEmptyFallback_PreservesReferencesAndReportsDisagreement()
    {
        var error = Assert.Throws<IncompleteReferencesException>(() => DocumentReferences.Read(
            new Document(new([]), new([new Descriptor("fallback.ipt")]))));
        Assert.Equal("fallback.ipt", Assert.Single(error.References).Name);
        Assert.Contains("пустой коллекции", error.Message);
    }

    [Fact]
    public void RelativeName_IsEnoughForMissingReference()
    {
        var refs = DocumentReferences.Read(new Document(new([new Descriptor(null, "sub/part.ipt")]), new([])));
        Assert.Equal("part.ipt", Assert.Single(refs).Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailureOrPartialRead_PreservesKnownReferences_ButFallbackDoesNotProveCompleteness(bool failPrimary)
    {
        var doc = new Document(new([new Descriptor("known.ipt"), new Descriptor("lost.ipt")], 1),
            new([new Descriptor("fallback.ipt")]), failPrimary);
        var error = Assert.Throws<IncompleteReferencesException>(() => DocumentReferences.Read(doc));
        Assert.Contains(error.References, r => r.Name == "fallback.ipt");
        if (!failPrimary) Assert.Contains(error.References, r => r.Name == "known.ipt");
        Assert.DoesNotContain(error.References, r => r.Name == "lost.ipt");
    }

    [Fact]
    public void AlternateCollectionAccessor_CanRecoverWithoutFalseWarning()
    {
        var refs = DocumentReferences.Read(new Document(new([new Descriptor("part.ipt")], failIndexer: true), new([])));
        Assert.Equal("part.ipt", Assert.Single(refs).Name);
    }

    [Fact]
    public void BothSourcesFail_ReportsIncompleteInsteadOfNoDependencies()
    {
        var error = Assert.Throws<IncompleteReferencesException>(() => DocumentReferences.Read(
            new Document(new([]), new([new Descriptor("unreadable.ipt")], 0), true)));
        Assert.Empty(error.References);
        Assert.Contains("primary failure", error.Message);
        Assert.Contains("0 из 1", error.Message);
    }

    [Fact]
    public void NamelessDescriptor_IsReported()
    {
        var error = Assert.Throws<IncompleteReferencesException>(() => DocumentReferences.Read(
            new Document(new([new Descriptor(null)]), new([]))));
        Assert.Contains("имя ссылки", error.Message);
    }

    [Fact]
    public void PartialComposition_IsCopiedWithWarningInReport()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root.FullName, "source"));
            File.WriteAllText(Path.Combine(source.FullName, "asm.iam"), "a");
            File.WriteAllText(Path.Combine(source.FullName, "part.txt"), "p");
            var doc = new Document(new([new Descriptor("part.txt"), new Descriptor("lost.ipt")], 1), new([]));
            var collector = new Collector(source.FullName);
            var result = collector.Collect(["asm.iam"], new Reader(doc), _ => { });
            Assert.Equal(2, result.Found.Count);
            Assert.Contains("не полностью", Assert.Single(result.Warnings));
            var report = collector.Copy(result, Path.Combine(root.FullName, "copy"));
            Assert.Contains("не полностью", File.ReadAllText(report));
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scan_ReportsInaccessibleDirectory_AndContinuesOtherDirectories(bool disappears)
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var blocked = root.CreateSubdirectory("blocked").FullName;
            var good = root.CreateSubdirectory("good").FullName;
            var path = Path.Combine(good, "very-long-name.ipt");
            File.WriteAllText(path, "p");
            var scan = ProjectFiles.Scan(root.FullName, folder => folder == blocked
                ? throw (disappears ? new DirectoryNotFoundException("gone") : new UnauthorizedAccessException("denied"))
                : Directory.EnumerateFileSystemEntries(folder));
            Assert.Equal([path], scan.Files);
            Assert.Contains(blocked, Assert.Single(scan.Warnings));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void Scan_PartialEnumeration_KeepsAlreadyFoundFilesAndReportsFolder()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var part = Path.Combine(root.FullName, "part.ipt");
            File.WriteAllText(part, "p");
            IEnumerable<string> Entries(string folder)
            {
                yield return part;
                throw new IOException("enumeration failed");
            }
            var scan = ProjectFiles.Scan(root.FullName, Entries);
            Assert.Equal([part], scan.Files);
            Assert.Contains(root.FullName, Assert.Single(scan.Warnings));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void Scan_EmptyAccessibleFolder_HasNoWarnings()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var scan = ProjectFiles.Scan(root.FullName);
            Assert.Empty(scan.Files);
            Assert.Empty(scan.Warnings);
        }
        finally { root.Delete(true); }
    }
}
