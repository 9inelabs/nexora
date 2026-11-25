using Avalonia;
using Avalonia.Controls;

namespace Nexora.App.Views;

/// <summary>The style controls for a Scripture, Lyrics or Announcements theme.</summary>
public partial class ThemeEditorPanel : UserControl
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<ThemeEditorPanel, string>(nameof(Kind), "Scripture");

    /// <summary>Scripture, Lyrics or Announcements: hides the controls that do not apply.</summary>
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    /// <summary>Raised when "Choose a picture…" is pressed.</summary>
    public event Action? PickImageRequested;

    public ThemeEditorPanel()
    {
        InitializeComponent();
        PickImage.Click += (_, _) => PickImageRequested?.Invoke();
        Apply();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty) Apply();
    }

    private void Apply()
    {
        var scripture = Kind == "Scripture";
        VerseNumbersSection.IsVisible = scripture;
        RefOptions.IsVisible = scripture;
        TransitionSection.IsVisible = Kind != "Lyrics"; // lyrics use their own animations
        RefHeader.Text = Kind switch { "Lyrics" => "COPYRIGHT LINE", "Announcements" => "DATE LINE", _ => "REFERENCE LINE" };
        CameraOption.IsVisible = Kind != "Announcements";
    }
}
