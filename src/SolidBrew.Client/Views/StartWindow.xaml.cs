using System.Windows;

namespace SolidBrew.Client.Views;

public partial class StartWindow : Window
{
    public StartWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Search.Focus();
    }
}
