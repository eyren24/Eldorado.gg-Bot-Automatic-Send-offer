using System.Windows;
using System.Windows.Controls;
using EldoradoApp.ViewModels;

namespace EldoradoApp.Views.Pages;

/// <summary>Sign-in (email/password, Google, pasted token), polling options and category switches.</summary>
public partial class AccountPage : UserControl
{
    public AccountPage() => InitializeComponent();

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        // PasswordBox.Password isn't bindable; hand it to the command here.
        if (DataContext is AccountViewModel vm && vm.SignInCommand.CanExecute(PasswordBox.Password))
        {
            vm.SignInCommand.Execute(PasswordBox.Password);
        }
    }

}
