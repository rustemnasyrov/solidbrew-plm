using System.Windows.Controls;
using System.Windows.Input;
using SolidBrew.Client.ViewModels;

namespace SolidBrew.Client.Views;

public partial class BrowserView : UserControl
{
    public BrowserView()
    {
        InitializeComponent();
    }

    // Двойной щелчок по документу — карточка документа.
    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is BrowserViewModel vm && vm.ShowDocumentCommand.CanExecute(null)) vm.ShowDocumentCommand.Execute(null);
    }
}
