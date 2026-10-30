using System.Windows;

namespace PdmLite.UI.Shared;

public static class UiMessages
{
    public const string Caption = "PdmLite";

    public static void Error(string message, Exception? ex = null) =>
        MessageBox.Show(ex is null ? message : $"{message}\n\n{ex.Message}", Caption, MessageBoxButton.OK, MessageBoxImage.Error);

    public static void Info(string message) =>
        MessageBox.Show(message, Caption, MessageBoxButton.OK, MessageBoxImage.Information);
}
