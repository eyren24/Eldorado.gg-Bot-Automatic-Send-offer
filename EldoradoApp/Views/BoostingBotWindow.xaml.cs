using System.Windows;
using EldoradoApp.ViewModels;

namespace EldoradoApp.Views;

public partial class BoostingBotWindow : Window
{
    public BoostingBotWindow()
    {
        InitializeComponent();
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        // PasswordBox.Password isn't bindable; hand it to the command here.
        if (DataContext is BoostingBotViewModel vm && vm.SignInCommand.CanExecute(PasswordBox.Password))
        {
            vm.SignInCommand.Execute(PasswordBox.Password);
        }
    }

    private async void SignInGoogle_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not BoostingBotViewModel vm)
        {
            return;
        }

        // The view owns the browser window; the VM/backend own the OAuth logic.
        await vm.SignInWithGoogleAsync(authorizeUrl =>
        {
            var login = new GoogleLoginWindow(authorizeUrl) { Owner = this };
            login.ShowDialog();
            return (login.AuthorizationCode, login.Error);
        });
    }
}
