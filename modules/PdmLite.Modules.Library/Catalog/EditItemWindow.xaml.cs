using System.Windows;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Catalog;

public partial class EditItemWindow : Window
{
    private readonly EditItemViewModel _viewModel;

    public EditItemWindow(EditItemViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        var error = await _viewModel.SaveAsync();
        IsEnabled = true;
        if (error is not null)
        {
            UiMessages.Error("Не удалось сохранить изменения.\n\n" + error);
            return;
        }
        DialogResult = true;
    }
}
