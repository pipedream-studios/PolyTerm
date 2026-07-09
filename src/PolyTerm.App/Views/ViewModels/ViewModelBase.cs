using CommunityToolkit.Mvvm.ComponentModel;

namespace PolyTerm.App.ViewModels;

/// <summary>Common base class for all PolyTerm view models; provides <c>INotifyPropertyChanged</c> via CommunityToolkit.Mvvm.</summary>
public abstract class ViewModelBase : ObservableObject { }
