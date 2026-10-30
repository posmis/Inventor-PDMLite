using PdmLite.Core.Domain;

namespace PdmLite.Core.UI;

/// <summary>
/// Ключи иконок PdmLite. Сами иконки — в <c>PdmLite.UI.Shared/Themes/Icons.xaml</c>
/// (генерируются из <c>tools/icons/icons.mjs</c>); ядро знает только ключи.
/// </summary>
public static class IconKeys
{
    // Документы
    public const string Part = "Doc.Part";
    public const string Assembly = "Doc.Assembly";
    public const string Drawing = "Doc.Drawing";
    public const string Presentation = "Doc.Presentation";
    public const string Project = "Doc.Project";
    public const string UnknownDocument = "Doc.Unknown";
    public const string Folder = "Folder";
    public const string FolderOpen = "Folder.Open";

    // Команды
    public const string Refresh = "Cmd.Refresh";
    public const string Search = "Cmd.Search";
    public const string CollapseAll = "Cmd.CollapseAll";
    public const string FollowActive = "Cmd.FollowActive";
    public const string Open = "Cmd.Open";
    public const string OpenFolder = "Cmd.OpenFolder";
    public const string OpenProjectFile = "Cmd.OpenProjectFile";
    public const string Recent = "Cmd.Recent";
    public const string ShowInWindows = "Cmd.ShowInWindows";
    public const string CopyPath = "Cmd.CopyPath";
    public const string ProjectTree = "Cmd.ProjectTree";
    public const string RevealInExplorer = "Cmd.RevealInExplorer";
    public const string InventorProject = "Cmd.InventorProject";
    public const string Modules = "Cmd.Modules";
    public const string Info = "Cmd.Info";

    // Состояния
    public const string Online = "State.Online";
    public const string Offline = "State.Offline";
    public const string Warning = "State.Warning";
    public const string Error = "State.Error";

    public const string App = "App.PdmLite";

    public static string ForKind(DocumentKind kind) => kind switch
    {
        DocumentKind.Folder => Folder,
        DocumentKind.Part => Part,
        DocumentKind.Assembly => Assembly,
        DocumentKind.Drawing => Drawing,
        DocumentKind.Presentation => Presentation,
        DocumentKind.Project => Project,
        _ => UnknownDocument,
    };
}
