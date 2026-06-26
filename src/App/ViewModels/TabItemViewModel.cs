using CommunityToolkit.Mvvm.ComponentModel;

namespace ModelCodex.App.ViewModels;

/// <summary>Base for anything hosted in the main TabControl.</summary>
public abstract partial class TabItemViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = "";
    public virtual bool CanClose => true;
}
