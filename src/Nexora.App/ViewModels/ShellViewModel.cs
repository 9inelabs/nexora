using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Controls;
using Nexora.Core.Model;

namespace Nexora.App.ViewModels;

/// <summary>The working frame after sign-in: side rail with the open sections, Live Output and Settings.</summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly IReadOnlyList<SectionKind> _sections;

    public ShellViewModel(MainViewModel main, IReadOnlyList<SectionKind> sections, bool liveOutput, bool settings)
    {
        _main = main;
        _sections = sections;
        foreach (var k in sections)
            Pages.Add(new RailItem(Icons.ForSection(k), Sections.Get(k).Title,
                PageFor(k)));
        if (liveOutput || (sections.Count == 0 && !settings && Live.User?.MayRunLiveOutput == true))
            Pages.Insert(0, new RailItem("live", "Live Output", new LiveOutputViewModel()));
        if (Live.User?.IsAdmin == true)
            SettingsItem = new RailItem("settings", "Settings", null);

        if (Live.Client is { } c && sections.Count > 0) _ = c.OpenSectionsAsync(sections);
        else if (Live.Client is { } c2) _ = c2.OpenSectionsAsync([]);

        Live.PropertyChanged += OnLive;
        if (settings && SettingsItem is not null) Select(SettingsItem);
        else if (Pages.Count > 0) Select(Pages[0]);
        else if (SettingsItem is not null) Select(SettingsItem);
    }

    private static ViewModelBase PageFor(SectionKind k) => k switch
    {
        SectionKind.Scripture => new ScriptureSectionViewModel(),
        SectionKind.Lyrics => new LyricsSectionViewModel(),
        SectionKind.Announcements => new AnnouncementsSectionViewModel(),
        SectionKind.LowerThirds => new LowerThirdsSectionViewModel(),
        SectionKind.Countdown => new CountdownSectionViewModel(),
        SectionKind.MinisterTimer => new MinisterTimerSectionViewModel(),
        SectionKind.Camera => new CameraSectionViewModel(),
        _ => new TestSlideSectionViewModel(k),
    };

    public ObservableCollection<RailItem> Pages { get; } = [];
    public RailItem? SettingsItem { get; }
    public bool HasSettings => SettingsItem is not null;

    [ObservableProperty] private RailItem? _selected;
    [ObservableProperty] private ViewModelBase? _page;

    public string Title => Selected?.Title ?? "";
    public string Subtitle => Page switch
    {
        LiveOutputViewModel => $"{S.Config.PcName} · {(S.IsLiveOutputPc ? "server" : "remote control")}",
        SettingsViewModel => "Admin",
        _ => $"{Live.User?.Name} · {S.Config.PcName}",
    };

    public string UserName => Live.User?.Name ?? "";
    public string UserInitials => Live.User?.Initials ?? "";
    public string LinkText => Live.LinkText;
    public bool LinkOk => Live.IsConnected;
    public bool LinkLost => !Live.IsConnected;
    public bool VersionMismatch => Live.VersionMismatch;
    public string VersionText => $"This PC runs NEXORA {Nexora.App.Services.AppServices.Version}, the Live Output PC runs {Live.ServerVersion}. Install the same version on every PC before going live.";

    [RelayCommand]
    private void Select(RailItem item)
    {
        if (item == SettingsItem && item.Page is null)
            item.Page = new SettingsViewModel(_main);
        foreach (var p in Pages) p.IsSelected = p == item;
        if (SettingsItem is not null) SettingsItem.IsSelected = SettingsItem == item;
        Selected = item;
        Page = item.Page;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
    }

    [RelayCommand]
    private void AddSections() => _main.GoSectionPicker(_sections);

    [RelayCommand]
    private Task SignOut() => _main.SignOutAsync();

    /// <summary>Keyboard: Ctrl+1..9 switch pages, everything else goes to the current page.</summary>
    public bool HandleKey(Key key, KeyModifiers mods)
    {
        if (mods.HasFlag(KeyModifiers.Control) && key is >= Key.D1 and <= Key.D9)
        {
            var i = key - Key.D1;
            if (i < Pages.Count) Select(Pages[i]);
            return true;
        }
        return Page is IKeyHandler h && h.HandleKey(key, mods);
    }

    private void OnLive(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Live.LinkText) or nameof(Live.IsConnected))
        {
            OnPropertyChanged(nameof(LinkText));
            OnPropertyChanged(nameof(LinkOk));
            OnPropertyChanged(nameof(LinkLost));
        }
        if (e.PropertyName == nameof(Live.VersionMismatch))
        {
            OnPropertyChanged(nameof(VersionMismatch));
            OnPropertyChanged(nameof(VersionText));
        }
    }

    public override void OnLeave()
    {
        Live.PropertyChanged -= OnLive;
        foreach (var p in Pages) p.Page?.OnLeave();
        SettingsItem?.Page?.OnLeave();
    }
}

public sealed partial class RailItem(string icon, string title, ViewModelBase? page) : ObservableObject
{
    public string Icon => icon;
    public string Title => title;
    public ViewModelBase? Page { get; set; } = page;
    [ObservableProperty] private bool _isSelected;
}
