// Подменяется только показ окон; тесты компилируют реальные Prompt и AssemblyCheckViewModel.
namespace PdmLite.Library.Tests
{
    internal sealed class PromptDialogs
    {
        public static readonly AsyncLocal<PromptDialogs?> Current = new();
        public int Questions, Errors;
        public string? LastError;
        public Action? OnQuestion;
        public readonly TaskCompletionSource ErrorShown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
namespace System.Windows
{
    public enum MessageBoxButton { OK, YesNo }
    public enum MessageBoxImage { Warning }
    public enum MessageBoxResult { OK, Yes, No }
    public static class MessageBox
    {
        public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage image)
        {
            var dialogs = PdmLite.Library.Tests.PromptDialogs.Current.Value!;
            dialogs.Questions++; dialogs.OnQuestion?.Invoke();
            return MessageBoxResult.Yes;
        }
    }
}
namespace PdmLite.UI.Shared
{
    public static class UiMessages
    {
        public const string Caption = "test";
        public static void Error(string message, Exception? error = null)
        {
            var dialogs = PdmLite.Library.Tests.PromptDialogs.Current.Value!;
            dialogs.Errors++; dialogs.LastError = message + error?.Message; dialogs.ErrorShown.TrySetResult();
        }
        public static void Info(string message) { }
    }
}
namespace PdmLite.Modules.Library.Catalog
{
    public sealed record ChangeLine(string A, string B, string C, string D);
    public sealed class ConfirmChangesWindow
    {
        public ConfirmChangesWindow(string title, string header, string totals, string notes, string okText,
            (string, string, string, string) columns, IReadOnlyList<ChangeLine> lines) { }
        public bool? ShowDialog() => true;
    }
}
