using System.Runtime.InteropServices;

namespace PdmLite.AddIn.Core;

/// <summary>Чтение iProperties открытых документов Inventor.</summary>
internal static class InventorProperties
{
    private const string DesignTrackingSet = "Design Tracking Properties";

    public static string? PartNumber(Inv._Document? document)
    {
        if (document is null)
            return null;
        try
        {
            foreach (Inv.PropertySet set in document.PropertySets)
            {
                if (set.Name != DesignTrackingSet)
                    continue;
                foreach (Inv.Property property in set)
                    if (property.Name == "Part Number")
                        return Convert.ToString(property.Value);
            }
        }
        catch (COMException)
        {
            // Документ не загружен (например, подавлен) — обозначение неизвестно.
        }
        return null;
    }
}
