using Microsoft.Extensions.Logging;
using PdmLite.Core.Events;

namespace PdmLite.Core.Documents;

/// <summary>Keep optional notifications from propagating failures into a native Inventor callback.</summary>
public static class ContentCenterNotification
{
    public static void PublishSafely(Func<bool> isContentCenter, Func<string?> assemblyPath,
        Func<string?> componentPath, Func<string?> partNumber,
        Action<ContentCenterComponentPlaced> publish, ILogger logger)
    {
        try
        {
            if (!isContentCenter()) return;
            var path = componentPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            var parent = assemblyPath();
            if (string.IsNullOrWhiteSpace(parent)) return;
            string? number = null;
            try { number = partNumber(); }
            catch (Exception ex) { logger.LogDebug(ex, "Обозначение вставленного компонента недоступно"); }
            publish(new ContentCenterComponentPlaced(parent, path, number));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Уведомление Content Center пропущено: данные вхождения недоступны");
        }
    }
}
