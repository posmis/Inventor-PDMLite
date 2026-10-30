using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Events;

namespace PdmLite.AddIn.Core;

/// <summary>
/// Перехватывает поиск файлов при открытии документов Inventor (FileAccessEvents.OnFileResolution)
/// и даёт модулям подставить файл вместо ненайденного — без окна «Файл не найден».
/// </summary>
public sealed class FileResolutionHook : IDisposable
{
    private readonly Inv.Application _inventor;
    private readonly Inv.FileAccessEvents _fileAccessEvents; // держим ссылку, иначе COM-подписка может быть собрана GC
    private readonly IServiceProvider _services;
    private readonly IEventBus _events;
    private readonly ILogger _logger;

    public FileResolutionHook(Inv.Application inventor, IServiceProvider services, IEventBus events, ILogger logger)
    {
        _inventor = inventor;
        _services = services;
        _events = events;
        _logger = logger;
        _fileAccessEvents = inventor.FileAccessEvents;
        _fileAccessEvents.OnFileResolution += OnFileResolution;
    }

    private void OnFileResolution(string relativeFileName, string libraryName, ref byte[] customLogicalName, Inv.EventTimingEnum beforeOrAfter,
        Inv.NameValueMap context, out string fullFileName, out Inv.HandlingCodeEnum handlingCode)
    {
        fullFileName = "";
        handlingCode = Inv.HandlingCodeEnum.kEventNotHandled;
        if (beforeOrAfter != Inv.EventTimingEnum.kBefore || string.IsNullOrEmpty(relativeFileName))
            return;

        try
        {
            var library = string.IsNullOrEmpty(libraryName) ? null : libraryName;
            foreach (var resolver in _services.GetServices<IMissingFileResolver>())
            {
                if (resolver.Resolve(relativeFileName, library) is not { } found)
                    continue;

                fullFileName = found;
                handlingCode = Inv.HandlingCodeEnum.kEventHandled;
                _logger.LogInformation("Ссылка {Reference} (библиотека {Library}) подменена: {Path}", relativeFileName, library, found);
                _inventor.StatusBarText = $"PdmLite: компонент библиотеки найден по новому пути — {Path.GetFileName(found)}. Сохраните сборку.";
                _events.Publish(new MissingFileResolved(relativeFileName, found));
                return;
            }
        }
        catch (Exception ex)
        {
            // Ошибка не должна мешать открытию документа — Inventor продолжит поиск сам.
            _logger.LogWarning(ex, "Подстановка файла {Reference}: ошибка", relativeFileName);
        }
    }

    public void Dispose() => _fileAccessEvents.OnFileResolution -= OnFileResolution;
}
