using System.Windows;
using EldoradoApp.ViewModels;

namespace EldoradoApp.Views;

/// <summary>
/// The gate: the only window that exists before the shell. It closes with
/// <c>DialogResult = true</c> once a key is accepted; closing it any other way ends the
/// app, so there is no path into the bot that skips the licence check.
/// </summary>
public partial class ActivationWindow : Window
{
    private LicenseViewModel? _model;

    public ActivationWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => KeyBox.Focus();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null)
        {
            _model.Activated -= OnActivated;
        }

        _model = DataContext as LicenseViewModel;

        if (_model is not null)
        {
            _model.Activated += OnActivated;
        }
    }

    private void OnActivated()
    {
        DialogResult = true;
        Close();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
