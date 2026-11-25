using CommunityToolkit.Mvvm.ComponentModel;
using Nexora.App.Services;

namespace Nexora.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    protected static AppServices S => AppServices.Instance;
    protected static LiveModel Live => AppServices.Instance.Live;

    /// <summary>Called when the screen is left, to unhook events.</summary>
    public virtual void OnLeave() { }
}

/// <summary>A page that wants the keyboard (arrow keys, Enter, F1–F8).</summary>
public interface IKeyHandler
{
    bool HandleKey(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers);
}
