using System.Text;

namespace ShortenNames;

/// <summary>
/// Сокращение слишком длинных имён файлов Inventor с перепривязкой ссылок. Отдельная утилита (не часть PdmLite):
/// своё окно, папку и ограничения задаёт пользователь, до переименования показывает «было → станет».
/// </summary>
internal static class Program
{
    internal static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdmLite", "logs",
        $"shorten-names-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    [STAThread]
    private static void Main(string[] args)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log("ОШИБКА: " + e.Exception);
            MessageBox.Show(e.Exception.Message, "Сокращение имён", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        Application.Run(new MainForm(args.FirstOrDefault(Directory.Exists)));
    }

    private static readonly object LogLock = new();

    public static void Log(string text)
    {
        lock (LogLock)
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {text}{Environment.NewLine}", Encoding.UTF8);
    }
}
