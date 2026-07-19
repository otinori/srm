using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Srm.PolicyEditor.Mvvm;

// CommunityToolkit.Mvvm等の外部依存を増やさず、最小限のMVVM基盤を自前実装する
// （DC-014: このプロジェクトは元々依存を最小限に保つ方針のため）。
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
