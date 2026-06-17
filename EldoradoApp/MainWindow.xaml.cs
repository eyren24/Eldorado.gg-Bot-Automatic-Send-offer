using System.Windows;
using EldoradoApp.Services;
using EldoradoApp.ViewModels;
using EldoradoApp.Views;

namespace EldoradoApp;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        // Returning from the bot window (e.g. after signing in) refreshes the feed.
        if (DataContext is RequestsFeedViewModel vm && vm.RefreshCommand.CanExecute(null))
        {
            vm.RefreshCommand.Execute(null);
        }
    }

    private void OnBotClick(object sender, RoutedEventArgs e)
    {
        var backend = ((App)Application.Current).Backend;
        var window = new BoostingBotWindow
        {
            DataContext = new BoostingBotViewModel(backend),
            Owner = this
        };
        window.Show();
    }
}
