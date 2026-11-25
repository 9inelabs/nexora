using System.Collections.ObjectModel;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.Core.Model;
using Nexora.Core.Protocol;

namespace Nexora.App.ViewModels;

/// <summary>
/// Lower Thirds: a minister's name and title over the camera. People are kept in a list; today's
/// speakers are one click (or F1–F8) away. A name hides itself after a few seconds unless told to stay.
/// </summary>
public sealed partial class LowerThirdsSectionViewModel : SectionBase, IKeyHandler
{
    private bool _loading;

    public LowerThirdsSectionViewModel() : base(SectionKind.LowerThirds)
    {
        Load();
    }

    public ObservableCollection<PersonRow> People { get; } = [];
    public ObservableCollection<PersonRow> Today { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _imageId;
    [ObservableProperty] private PersonRow? _editing;
    [ObservableProperty] private ScreenFrame? _previewFrame;
    [ObservableProperty] private LowerThirdStyle _style;
    [ObservableProperty] private string _accent = "#E9C77E";
    [ObservableProperty] private OverlayAnimation _animation;
    [ObservableProperty] private double _hideAfter = 8;
    [ObservableProperty] private bool _stay;
    [ObservableProperty] private bool _dirty;

    public static IReadOnlyList<LowerThirdStyle> Styles { get; } = Enum.GetValues<LowerThirdStyle>();
    public static IReadOnlyList<OverlayAnimation> Animations { get; } = Enum.GetValues<OverlayAnimation>();
    public bool HasToday => Today.Count > 0;
    public bool HasImage => !string.IsNullOrEmpty(ImageId);
    public Avalonia.Media.IBrush AccentBrush => Controls.ScriptureRenderer.Brush(Accent);

    private void Load()
    {
        _loading = true;
        var d = Live.LowerThirdDefaults;
        Style = d.Style;
        Accent = d.Accent;
        Animation = d.Animation;
        HideAfter = d.HideAfterSeconds <= 0 ? 8 : d.HideAfterSeconds;
        Stay = d.HideAfterSeconds <= 0;
        RefreshPeople();
        _loading = false;
        Dirty = false;
        UpdatePreview();
    }

    private void RefreshPeople()
    {
        People.Clear();
        var q = Nexora.Core.Model.SongText.Fold(Search);
        foreach (var p in Live.People.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            if (q.Length == 0 || Nexora.Core.Model.SongText.Fold(p.Name + " " + p.Title).Contains(q)) People.Add(new PersonRow(p));
        Today.Clear();
        var n = 1;
        foreach (var id in Live.TodayPeople)
            if (Live.People.FirstOrDefault(p => p.Id == id) is { } p) Today.Add(new PersonRow(p) { Key = n <= 8 ? $"F{n++}" : "" });
        OnPropertyChanged(nameof(HasToday));
    }

    partial void OnSearchChanged(string value) => RefreshPeople();
    partial void OnNameChanged(string value) => UpdatePreview();
    partial void OnTitleChanged(string value) => UpdatePreview();
    partial void OnImageIdChanged(string? value) { OnPropertyChanged(nameof(HasImage)); UpdatePreview(); }
    partial void OnStyleChanged(LowerThirdStyle value) => StyleEdited();
    partial void OnAccentChanged(string value) { OnPropertyChanged(nameof(AccentBrush)); StyleEdited(); }
    partial void OnAnimationChanged(OverlayAnimation value) => StyleEdited();
    partial void OnHideAfterChanged(double value) => StyleEdited();
    partial void OnStayChanged(bool value) => StyleEdited();

    private void StyleEdited()
    {
        if (_loading) return;
        Dirty = true;
        UpdatePreview();
    }

    private LowerThirdShow ShowFor(string name, string title, string? image) =>
        new(name.Trim(), title.Trim(), image, Style, Accent, Animation, Stay ? 0 : (int)HideAfter, DateTime.UtcNow);

    private void UpdatePreview()
    {
        var lt = ShowFor(Name.Length > 0 ? Name : "Pastor John Mensah", Name.Length > 0 ? Title : "Senior Pastor", ImageId);
        PreviewFrame = PreviewOf(new SlideContent(lt.Name, lt.Title, lt.Name, LowerThird: lt), Layer.Overlays, withCamera: true);
    }

    // ---------- Showing ----------

    [RelayCommand]
    private async Task Show()
    {
        if (Name.Trim().Length == 0) return;
        var lt = ShowFor(Name, Title, ImageId);
        await SendAsync(new SlideContent(lt.Name, lt.Title, lt.Name, LowerThird: lt));
    }

    [RelayCommand]
    private async Task ShowPerson(PersonRow row)
    {
        Pick(row);
        await Show();
    }

    [RelayCommand]
    private Task Hide() => ClearAsync();

    [RelayCommand]
    private void Pick(PersonRow row)
    {
        Name = row.Person.Name;
        Title = row.Person.Title;
        ImageId = row.Person.ImageId;
        Editing = row;
    }

    // ---------- The people list ----------

    [RelayCommand]
    private async Task SavePerson()
    {
        if (Name.Trim().Length == 0 || Live.Client is not { } c) return;
        var list = Live.People.ToList();
        var existing = Editing is { } e ? list.FindIndex(p => p.Id == e.Person.Id) : list.FindIndex(p => p.Name.Equals(Name.Trim(), StringComparison.OrdinalIgnoreCase));
        var person = new Person(existing >= 0 ? list[existing].Id : "p-" + Guid.NewGuid().ToString("N")[..10], Name.Trim(), Title.Trim(), ImageId);
        if (existing >= 0) list[existing] = person;
        else list.Add(person);
        await c.SendAsync(new SavePeople(list, Live.TodayPeople, Defaults()));
        Live.RaiseToast($"Saved {person.Name}.");
    }

    [RelayCommand]
    private async Task DeletePerson(PersonRow row)
    {
        if (Live.Client is not { } c) return;
        await c.SendAsync(new SavePeople(Live.People.Where(p => p.Id != row.Person.Id).ToList(), Live.TodayPeople.Where(id => id != row.Person.Id).ToList(), Defaults()));
    }

    [RelayCommand]
    private async Task AddToday(PersonRow row)
    {
        if (Live.Client is not { } c || Live.TodayPeople.Contains(row.Person.Id)) return;
        await c.SendAsync(new SavePeople(Live.People, Live.TodayPeople.Append(row.Person.Id).ToList(), Defaults()));
    }

    [RelayCommand]
    private async Task RemoveToday(PersonRow row)
    {
        if (Live.Client is not { } c) return;
        await c.SendAsync(new SavePeople(Live.People, Live.TodayPeople.Where(id => id != row.Person.Id).ToList(), Defaults()));
    }

    [RelayCommand]
    private async Task ClearToday()
    {
        if (Live.Client is { } c) await c.SendAsync(new SavePeople(Live.People, [], Defaults()));
    }

    [RelayCommand]
    private void NewPerson()
    {
        Editing = null;
        Name = Title = "";
        ImageId = null;
    }

    [RelayCommand]
    private void ClearPhoto() => ImageId = null;

    private LowerThirdDefaults Defaults() => new(Style, Accent, Animation, Stay ? 0 : (int)HideAfter);

    [RelayCommand]
    private async Task SaveStyle()
    {
        if (Live.Client is not { } c) return;
        await c.SendAsync(new SavePeople(Live.People, Live.TodayPeople, Defaults()));
        Dirty = false;
        Live.RaiseToast("The lower-third style is saved for every PC.");
    }

    public async Task UsePhotoAsync(string path)
    {
        if (Live.Client is not { } c) return;
        var name = await c.UploadAsync("media", Path.GetFileName(path), path);
        var local = Path.Combine(Nexora.Core.Config.AppPaths.MediaDir, name);
        if (!File.Exists(local)) File.Copy(path, local, overwrite: true);
        ImageId = name;
    }

    public bool HandleKey(Key key, KeyModifiers mods)
    {
        if (key is >= Key.F1 and <= Key.F8)
        {
            var i = key - Key.F1;
            if (i < Today.Count) _ = ShowPerson(Today[i]);
            return true;
        }
        switch (key)
        {
            case Key.Enter: _ = Show(); return true;
            case Key.Escape: _ = Hide(); return true;
        }
        return false;
    }

    protected override void OnLive(string? property)
    {
        if (property != nameof(Live.Library)) return;
        if (Dirty) RefreshPeople();
        else Load();
    }
}

public sealed partial class PersonRow(Person person) : ObservableObject
{
    public Person Person => person;
    public string Name => person.Name;
    public string Title => person.Title;
    public string Key { get; init; } = "";
    public bool HasKey => Key.Length > 0;
}
