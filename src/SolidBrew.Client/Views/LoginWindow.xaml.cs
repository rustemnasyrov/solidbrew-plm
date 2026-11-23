using System.Windows;
using SolidBrew.Client.ViewModels;

namespace SolidBrew.Client.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
    }

    // PasswordBox не поддерживает привязку — передаём пароль в ViewModel вручную.
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm) vm.Password = PasswordInput.Password;
    }
}
