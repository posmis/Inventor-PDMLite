using PdmLite.Core.Documents;

namespace PdmLite.Interop;

/// <summary>
/// Чтение и запись iProperties через наборы свойств Inventor — общий код для Apprentice (Explorer)
/// и Inventor API (плагин). Подключается в оба проекта ссылкой на файл: наборы свойств у них одни.
/// </summary>
internal static class InventorPropertySets
{
    public const string SummarySet = "Inventor Summary Information";
    public const string DesignTrackingSet = "Design Tracking Properties";
    public const string UserDefinedSet = "Inventor User Defined Properties";
    public const string ContentCenterSet = "Content Library Component Properties";

    /// <summary>Свойства, ссылки и геометрия документа.</summary>
    public static DocumentProperties Read(Inv.PropertySets sets, Inv.File file, (double? Volume, double? Area) geometry)
    {
        var custom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (FindSet(sets, UserDefinedSet) is { } user)
            foreach (Inv.Property property in user)
                custom[property.Name] = Convert.ToString(property.Value) ?? "";

        var references = new List<string>();
        foreach (Inv.FileDescriptor descriptor in file.ReferencedFileDescriptors)
            if (!string.IsNullOrEmpty(descriptor.FullFileName))
                references.Add(descriptor.FullFileName);

        return new DocumentProperties
        {
            PartNumber = GetValue(sets, DesignTrackingSet, "Part Number"),
            Description = GetValue(sets, DesignTrackingSet, "Description"),
            Title = GetValue(sets, SummarySet, "Title"),
            IsContentCenter = FindSet(sets, ContentCenterSet) is not null,
            Custom = custom,
            References = references,
            Volume = geometry.Volume,
            Area = geometry.Area,
        };
    }

    /// <summary>Записывает изменения в наборы свойств (сохраняет файл вызывающий).</summary>
    public static void Apply(Inv.PropertySets sets, DocumentPropertyChanges changes)
    {
        if (changes.PartNumber is not null)
            SetValue(sets, DesignTrackingSet, "Part Number", changes.PartNumber);
        if (changes.Description is not null)
            SetValue(sets, DesignTrackingSet, "Description", changes.Description);
        if (changes.Title is not null)
            SetValue(sets, SummarySet, "Title", changes.Title);

        // Метку Content Center снимаем удалением её набора свойств.
        if (changes.RemoveContentCenterLink && FindSet(sets, ContentCenterSet) is { } contentCenter)
        {
            try
            {
                contentCenter.Delete();
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                throw new InvalidOperationException($"Не удалось снять метку Content Center: {ex.Message}", ex);
            }
        }

        if (changes.Custom.Count > 0)
        {
            var user = FindSet(sets, UserDefinedSet) ?? throw new InvalidOperationException("Нет набора пользовательских свойств.");
            foreach (var (name, value) in changes.Custom)
            {
                if (FindProperty(user, name) is { } property)
                    property.Value = value;
                else
                    user.Add(value, name, Type.Missing);
            }
        }
    }

    /// <summary>
    /// Объём и площадь поверхности из масс-свойств (для поиска одинаковой геометрии). Позднее
    /// связывание: свойство есть у деталей и сборок; не прочиталось — <c>null</c>, проверка пропускается.
    /// </summary>
    public static (double? Volume, double? Area) Geometry(object document, Action<Exception> onError)
    {
        try
        {
            dynamic doc = document;
            dynamic mass = doc.ComponentDefinition.MassProperties;
            return ((double)mass.Volume, (double)mass.Area);
        }
        catch (Exception ex)
        {
            onError(ex);
            return (null, null);
        }
    }

    public static Inv.PropertySet? FindSet(Inv.PropertySets sets, string name)
    {
        foreach (Inv.PropertySet set in sets)
            if (set.Name == name)
                return set;
        return null;
    }

    public static Inv.Property? FindProperty(Inv.PropertySet set, string name)
    {
        foreach (Inv.Property property in set)
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property;
        return null;
    }

    private static string? GetValue(Inv.PropertySets sets, string setName, string propertyName) =>
        FindSet(sets, setName) is { } set && FindProperty(set, propertyName) is { } property
            ? Convert.ToString(property.Value)
            : null;

    private static void SetValue(Inv.PropertySets sets, string setName, string propertyName, string value)
    {
        var property = FindSet(sets, setName) is { } set ? FindProperty(set, propertyName) : null;
        if (property is null)
            throw new InvalidOperationException($"Нет свойства «{propertyName}».");
        property.Value = value;
    }
}
