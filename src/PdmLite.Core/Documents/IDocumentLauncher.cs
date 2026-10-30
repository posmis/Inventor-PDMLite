namespace PdmLite.Core.Documents;

/// <summary>
/// Открытие документа. Реализацию выбирает хост: внутри Inventor — через Inventor API,
/// во внешнем приложении — через связь с Inventor или ассоциацию файла в Windows.
/// </summary>
public interface IDocumentLauncher
{
    Task OpenAsync(string path, CancellationToken cancellationToken = default);
}
