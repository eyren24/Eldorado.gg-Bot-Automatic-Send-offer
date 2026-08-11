using CommunityToolkit.Mvvm.ComponentModel;
using EldoradoApp.Models;

namespace EldoradoApp.ViewModels;

/// <summary>Editable row for one surcharge (DuoQ, streaming, …), writing through to the model.</summary>
public sealed partial class ExtraOptionRow : ObservableObject
{
    private readonly Action _onChanged;

    public ExtraOption Model { get; }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _label;
    [ObservableProperty] private string _keywords;
    [ObservableProperty] private ExtraKind _kind;
    [ObservableProperty] private decimal _amount;
    [ObservableProperty] private bool _alwaysApply;

    public ExtraOptionRow(ExtraOption model, Action onChanged)
    {
        Model = model;
        _onChanged = onChanged;
        _enabled = model.Enabled;
        _label = model.Label;
        _keywords = model.Keywords;
        _kind = model.Kind;
        _amount = model.Amount;
        _alwaysApply = model.AlwaysApply;
    }

    partial void OnEnabledChanged(bool value) => Push(() => Model.Enabled = value);
    partial void OnLabelChanged(string value) => Push(() => Model.Label = value);
    partial void OnKeywordsChanged(string value) => Push(() => Model.Keywords = value);
    partial void OnKindChanged(ExtraKind value) => Push(() => Model.Kind = value);
    partial void OnAmountChanged(decimal value) => Push(() => Model.Amount = value);
    partial void OnAlwaysApplyChanged(bool value) => Push(() => Model.AlwaysApply = value);

    private void Push(Action write)
    {
        write();
        _onChanged();
    }
}
