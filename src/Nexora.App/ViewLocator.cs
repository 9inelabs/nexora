using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Nexora.App.ViewModels;

namespace Nexora.App;

/// <summary>Finds the view for a view model by name: FooViewModel → FooView.</summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Func<Control>> Views = new()
    {
        [typeof(FirstRunViewModel)] = () => new Views.FirstRunView(),
        [typeof(LoginViewModel)] = () => new Views.LoginView(),
        [typeof(SectionPickerViewModel)] = () => new Views.SectionPickerView(),
        [typeof(ShellViewModel)] = () => new Views.ShellView(),
        [typeof(TestSlideSectionViewModel)] = () => new Views.TestSlideSectionView(),
        [typeof(ScriptureSectionViewModel)] = () => new Views.ScriptureSectionView(),
        [typeof(LyricsSectionViewModel)] = () => new Views.LyricsSectionView(),
        [typeof(AnnouncementsSectionViewModel)] = () => new Views.AnnouncementsSectionView(),
        [typeof(LowerThirdsSectionViewModel)] = () => new Views.LowerThirdsSectionView(),
        [typeof(CountdownSectionViewModel)] = () => new Views.CountdownSectionView(),
        [typeof(LiveOutputViewModel)] = () => new Views.LiveOutputView(),
        [typeof(SettingsViewModel)] = () => new Views.SettingsView(),
        [typeof(StartupErrorViewModel)] = () => new Views.StartupErrorView(),
    };

    public Control Build(object? data)
    {
        if (data is not null && Views.TryGetValue(data.GetType(), out var make)) return make();
        return new TextBlock { Text = "Missing view for " + data?.GetType().Name };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
