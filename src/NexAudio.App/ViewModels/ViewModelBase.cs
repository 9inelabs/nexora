using CommunityToolkit.Mvvm.ComponentModel;

namespace NexAudio.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    protected static DeskServices S => DeskServices.Instance;
    protected static Session Live => DeskServices.Instance.Live;

    /// <summary>Called when the screen is left, to unhook events.</summary>
    public virtual void OnLeave() { }
}
