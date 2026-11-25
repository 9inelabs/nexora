using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Controls;
using Nexora.Core.Model;

namespace Nexora.App.ViewModels;

/// <summary>"What are you running today?" Pick one or more sections.</summary>
public sealed partial class SectionPickerViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public SectionPickerViewModel(MainViewModel main, IEnumerable<SectionKind>? preselect)
    {
        _main = main;
        var user = Live.User;
        var pre = preselect?.ToHashSet() ?? [];
        foreach (var def in Sections.Pickable)
        {
            var card = new SectionCard(def, user is not null && Sections.UserMayOpen(user.Roles, def.Kind));
            card.IsSelected = card.IsAllowed && pre.Contains(def.Kind);
            card.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SectionCard.IsSelected)) RaiseSummary();
            };
            Cards.Add(card);
        }
        Live.PropertyChanged += OnLive;
        UpdateInUse();
    }

    public ObservableCollection<SectionCard> Cards { get; } = [];

    public UserInfo? User => Live.User;
    public string Greeting
    {
        get
        {
            var h = DateTime.Now.Hour;
            var part = h < 12 ? "Good morning" : h < 17 ? "Good afternoon" : "Good evening";
            return $"{part}, {User?.Name.Split(' ')[0]}.";
        }
    }

    public string ChurchName => Live.Branding.ChurchName;
    public string UserName => User?.Name ?? "";
    public string UserInitials => User?.Initials ?? "";
    public string UserTitle => string.IsNullOrWhiteSpace(User?.Title) ? RoleText(User) : User!.Title;
    public string LinkText => Live.LinkText;
    public bool LinkOk => Live.IsConnected;

    public bool CanOpenLiveOutput => User?.MayRunLiveOutput == true;
    public bool IsAdmin => User?.IsAdmin == true;
    public bool IsTrainee => User?.IsTrainee == true;

    public IReadOnlyList<SectionKind> Selected => Cards.Where(c => c.IsSelected).Select(c => c.Kind).ToList();
    public bool HasSelection => Cards.Any(c => c.IsSelected);
    public string Summary
    {
        get
        {
            var sel = Cards.Where(c => c.IsSelected).ToList();
            return sel.Count == 0 ? "Nothing selected yet" : $"{sel.Count} selected · {string.Join(", ", sel.Select(c => c.Title))}";
        }
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasSelection));
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Toggle(SectionCard card)
    {
        if (card.IsAllowed) card.IsSelected = !card.IsSelected;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Start() => _main.GoShell(Selected, liveOutput: false);

    [RelayCommand]
    private void OpenLiveOutput() => _main.GoShell(Selected, liveOutput: true);

    [RelayCommand]
    private void OpenSettings() => _main.GoShell(Selected, liveOutput: false, settings: true);

    [RelayCommand]
    private Task SignOut() => _main.SignOutAsync();

    private void OnLive(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Live.Snapshot)) UpdateInUse();
        if (e.PropertyName == nameof(Live.LinkText)) { OnPropertyChanged(nameof(LinkText)); OnPropertyChanged(nameof(LinkOk)); }
    }

    private void UpdateInUse()
    {
        var myPc = S.Config.PcId;
        foreach (var card in Cards)
        {
            var other = Live.Snapshot.Sources.FirstOrDefault(s => s.Section == card.Kind && s.Online && s.PcId != myPc);
            card.InUseBy = other is null ? null : $"In use · {other.UserName} · {other.PcName}";
        }
    }

    public static string RoleText(UserInfo? u)
    {
        if (u is null) return "";
        if (u.IsAdmin) return "Admin";
        if (u.IsTrainee) return "Trainee";
        if (u.Roles.HasFlag(Role.LiveOutput)) return "Live Output operator";
        return "Operator";
    }

    public override void OnLeave() => Live.PropertyChanged -= OnLive;
}

public sealed partial class SectionCard(SectionDefinition def, bool allowed) : ObservableObject
{
    public SectionKind Kind => def.Kind;
    public string Title => def.Title;
    public string Description => def.Description;
    public string Icon => Icons.ForSection(def.Kind);
    public bool IsAllowed => allowed;
    public bool IsLocked => !allowed;

    [ObservableProperty] private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsFree), nameof(IsInUse))]
    private string? _inUseBy;

    public string StatusText => !allowed ? "Not in your role" : InUseBy ?? "Free";
    public bool IsFree => allowed && InUseBy is null;
    public bool IsInUse => allowed && InUseBy is not null;
}
