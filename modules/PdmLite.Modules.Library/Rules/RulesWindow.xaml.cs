using System.Windows;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Rules;

public partial class RulesWindow : Window
{
    private readonly RulesViewModel _viewModel;

    public RulesWindow(RulesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        Loaded += async (_, _) =>
        {
            try
            {
                await _viewModel.LoadAsync();
            }
            catch (Exception ex)
            {
                UiMessages.Error("Не удалось загрузить правила.", ex);
                Close();
            }
        };
    }

    private void Add_OnClick(object sender, RoutedEventArgs e)
    {
        var row = new RuleRowViewModel();
        _viewModel.Rows.Add(row);
        _viewModel.Selected = row;
    }

    private void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Selected is { } row)
            _viewModel.Rows.Remove(row);
    }

    private void Up_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Selected is { } row)
            _viewModel.Move(row, -1);
    }

    private void Down_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Selected is { } row)
            _viewModel.Move(row, 1);
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.SaveAsync();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            UiMessages.Error("Правила не сохранены.", ex);
        }
    }
}
