using System.Windows.Controls;
using System.Windows.Input;
using SolidBrew.Client.ViewModels;

namespace SolidBrew.Client.Views;

public partial class DocumentView : UserControl
{
    public DocumentView()
    {
        InitializeComponent();
    }

    private void OnRefDoubleClick(object sender, MouseButtonEventArgs e) => GoTo(RefsGrid.SelectedItem);

    private void OnUsedDoubleClick(object sender, MouseButtonEventArgs e) => GoTo(UsedGrid.SelectedItem);

    private void GoTo(object? row)
    {
        if (row != null && DataContext is DocumentViewModel vm) vm.GoToCommand.Execute(row);
    }
}
