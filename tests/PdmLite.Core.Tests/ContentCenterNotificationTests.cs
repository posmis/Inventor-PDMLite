using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Core.Events;

namespace PdmLite.Core.Tests;

public sealed class ContentCenterNotificationTests
{
    [Theory]
    [InlineData("classification")]
    [InlineData("component")]
    [InlineData("assembly")]
    [InlineData("publication")]
    public void FailingRequiredComAccessOrSubscriber_NeverEscapesNativeEvent(string stage)
    {
        var published = 0;
        ContentCenterNotification.PublishSafely(
            () => stage == "classification" ? throw new COMException("not ready") : true,
            () => stage == "assembly" ? throw new InvalidComObjectException("released") : "assembly.iam",
            () => stage == "component" ? throw new COMException("unloaded") : "part.ipt",
            () => "PN", _ => { if (stage == "publication") throw new InvalidOperationException("subscriber"); published++; },
            NullLogger.Instance);
        Assert.Equal(0, published);
    }

    [Fact]
    public void OptionalDocumentAccessFailure_StillPublishesWithoutPartNumber()
    {
        ContentCenterComponentPlaced? placed = null;
        ContentCenterNotification.PublishSafely(() => true, () => "assembly.iam", () => "part.ipt",
            () => throw new COMException("definition not loaded"), e => placed = e, NullLogger.Instance);
        Assert.NotNull(placed); Assert.Null(placed.PartNumber); Assert.Equal("part.ipt", placed.ComponentPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingComponentPath_DoesNotReadOtherComPropertiesOrPublish(string? path)
    {
        var reads = 0; var published = 0;
        ContentCenterNotification.PublishSafely(() => true,
            () => { reads++; return "assembly.iam"; }, () => path,
            () => { reads++; return "PN"; }, _ => published++, NullLogger.Instance);
        Assert.Equal(0, reads); Assert.Equal(0, published);
    }

    [Fact]
    public void NonContentCenterOccurrence_DoesNotReadComMetadata()
    {
        var reads = 0;
        ContentCenterNotification.PublishSafely(() => false,
            () => { reads++; return "assembly.iam"; }, () => { reads++; return "part.ipt"; },
            () => { reads++; return "PN"; }, _ => reads++, NullLogger.Instance);
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingAssemblyPath_DoesNotReadOptionalMetadataOrPublish(string? path)
    {
        var reads = 0; var published = 0;
        ContentCenterNotification.PublishSafely(() => true, () => path, () => "part.ipt",
            () => { reads++; return "PN"; }, _ => published++, NullLogger.Instance);
        Assert.Equal(0, reads); Assert.Equal(0, published);
    }

    [Fact]
    public void ValidOccurrence_PublishesItsData()
    {
        ContentCenterComponentPlaced? placed = null;
        ContentCenterNotification.PublishSafely(() => true, () => "assembly.iam", () => "part.ipt", () => "PN", e => placed = e, NullLogger.Instance);
        Assert.Equal(new("assembly.iam", "part.ipt", "PN"), placed);
    }
}
