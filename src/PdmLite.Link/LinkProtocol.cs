namespace PdmLite.Link;

/// <summary>Имена запросов и событий протокола.</summary>
public static class LinkProtocol
{
    /// <summary>Версия протокола; меняется при несовместимых изменениях.</summary>
    public const int Version = 1;

    public static class Requests
    {
        /// <summary>Проверка связи → <see cref="PingResult"/>.</summary>
        public const string Ping = "ping";

        // --- Inventor ---

        /// <summary>Состояние Inventor → <see cref="InventorStatus"/>.</summary>
        public const string GetStatus = "getStatus";

        /// <summary>Открыть документ в Inventor (<see cref="DocumentArgs"/>).</summary>
        public const string OpenDocument = "openDocument";

        /// <summary>Вставить компонент в активную сборку Inventor (<see cref="DocumentArgs"/>).</summary>
        public const string PlaceComponent = "placeComponent";

        /// <summary>Документы, загруженные в Inventor → <see cref="OpenDocumentsResult"/>.</summary>
        public const string GetOpenDocuments = "getOpenDocuments";

        // --- Explorer ---

        /// <summary>Открыть проект в Explorer (<see cref="ProjectArgs"/>).</summary>
        public const string OpenProject = "openProject";

        /// <summary>Показать документ в дереве Explorer (<see cref="RevealArgs"/>).</summary>
        public const string RevealDocument = "revealDocument";
    }

    public static class Events
    {
        /// <summary>Сменился активный документ Inventor (<see cref="DocumentArgs"/>, Path может быть null).</summary>
        public const string ActiveDocumentChanged = "activeDocumentChanged";

        /// <summary>Документ сохранён (<see cref="DocumentArgs"/>).</summary>
        public const string DocumentSaved = "documentSaved";

        /// <summary>Документ закрыт (<see cref="DocumentArgs"/>).</summary>
        public const string DocumentClosed = "documentClosed";
    }
}

public sealed record PingResult(string Application, string Version, int ProtocolVersion);

public sealed record InventorStatus(string InventorVersion, string? ProjectName, string? ProjectFile, string? WorkspaceFolder, string? ActiveDocument);

public sealed record DocumentArgs(string? Path);

/// <summary>Документ, загруженный в Inventor: изменён ли, открыт ли в своём окне.</summary>
public sealed record OpenDocumentEntry(string Path, bool IsDirty, bool IsVisible);

public sealed record OpenDocumentsResult(IReadOnlyList<OpenDocumentEntry> Documents);

public sealed record ProjectArgs(string Path);

public sealed record RevealArgs(string Path, string? ProjectPath);
