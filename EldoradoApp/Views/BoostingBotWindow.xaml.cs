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
}
