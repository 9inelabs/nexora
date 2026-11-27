using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Nexora.Core.Model;

namespace Nexora.App.Controls;

/// <summary>
/// The Minister Timer on the Stage Monitor: green, then yellow, then red, then over time.
/// The screen builds it once and calls <see cref="Parts.Update"/> on every tick, so the colours
/// change without rebuilding the screen.
/// </summary>
public static class MinisterRenderer
{
    private static double W => CanvasSize.W;
    private static double H => CanvasSize.H;
    private static readonly FontFamily Ui = new("avares://Nexora.UI/Assets/Fonts#Manrope");
    private static readonly FontFamily Mono = new("avares://Nexora.UI/Assets/Fonts#JetBrains Mono");

    public static readonly IBrush Green = new SolidColorBrush(Color.Parse("#3DD68C"));
    public static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#F0B44C"));
    public static readonly IBrush Red = new SolidColorBrush(Color.Parse("#FF5A5F"));
    private static readonly IBrush Black = new SolidColorBrush(Color.Parse("#0B0C0E"));
    private static readonly IBrush OverGround = new SolidColorBrush(Color.Parse("#2A0E10"));
    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#24262B"));
    private static readonly IBrush Title = new SolidColorBrush(Color.Parse("#C9CBD0"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8C8F96"));
    private static readonly IBrush OverDim = new SolidColorBrush(Color.Parse("#C9A0A2"));
    private static readonly IBrush OverText = new SolidColorBrush(Color.Parse("#FFB3B5"));
    private static readonly IBrush CloseText = new SolidColorBrush(Color.Parse("#FF8A8D"));
    private static readonly IBrush White = new SolidColorBrush(Color.Parse("#F2F2F0"));

    public static IBrush BrushFor(MinisterColour c) => c switch
    {
        MinisterColour.Green => Green,
        MinisterColour.Yellow => Yellow,
        _ => Red,
    };

    public static string TitleOf(MinisterTimerShow t) =>
        string.Join(" · ", new[] { t.Name, t.Purpose }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));

    /// <summary>The pieces that change as time passes.</summary>
    public sealed class Parts
    {
        public required Control Root { get; init; }
        public required MinisterTimerShow Show { get; init; }
        public required bool Use24Hour { get; init; }
        public Border? Frame { get; init; }
        public Border? Band { get; init; }
        public TextBlock? Clock { get; init; }
        public TextBlock? Label { get; init; }
        public TextBlock? Name { get; init; }
        public Border? Badge { get; init; }
        public required TextBlock Time { get; init; }
        public Border? Fill { get; init; }
        public double FillWidth { get; init; }
        public TextBlock? Message { get; init; }

        public void Update(DateTime utcNow)
        {
            var colour = Show.Colour(utcNow);
            var over = colour == MinisterColour.Over;
            var brush = BrushFor(colour);
            Time.Text = Show.Text(utcNow);
            if (Clock is not null) Clock.Text = Use24Hour ? DateTime.Now.ToString("HH:mm") : DateTime.Now.ToString("h:mm tt");
            if (Fill is not null)
            {
                Fill.Width = FillWidth * Show.Fraction(utcNow);
                Fill.Background = brush;
            }
            if (Message is not null)
            {
                Message.Text = Show.Message(utcNow, DateTime.Now, Use24Hour);
                Message.Foreground = over ? OverText : colour == MinisterColour.Red && Show.Seconds(utcNow) <= MinisterTimerShow.CloseAtSeconds ? CloseText : Dim;
                Message.FontWeight = colour is MinisterColour.Red or MinisterColour.Over ? FontWeight.Bold : FontWeight.Medium;
            }
            if (Frame is not null)
            {
                Frame.BorderBrush = brush;
                Frame.Background = over ? OverGround : Black;
            }
            if (Badge is not null) Badge.IsVisible = over;
            if (Name is not null) Name.IsVisible = !over;
            if (Label is not null) Label.Foreground = over ? OverDim : Dim;
            if (Band is not null) Band.Background = brush;
            if (Band is null) Time.Foreground = brush;

            // A gentle flash once time is up: the digits dim every other second.
            var flash = over && Show.Flash && Show.Running && DateTime.UtcNow.Second % 2 == 1;
            Time.Opacity = flash ? 0.45 : 1;
        }
    }

    /// <summary>The whole Stage Monitor becomes the timer.</summary>
    public static Parts FullScreen(MinisterTimerShow t, bool use24Hour, DateTime utcNow)
    {
        var frame = new Border { Width = W, Height = H, BorderThickness = new Thickness(21), Background = Black };
        var panel = new Panel();
        frame.Child = panel;

        var clock = new TextBlock
        {
            FontFamily = Mono, FontWeight = FontWeight.Medium, FontSize = 60, Foreground = White,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(39, 21, 0, 0),
        };
        var label = new TextBlock
        {
            Text = "MINISTER TIMER", FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = 22, LetterSpacing = 4.5,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 39, 39, 0),
        };
        panel.Children.Add(clock);
        panel.Children.Add(label);

        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(column);

        var title = TitleOf(t);
        var name = new TextBlock
        {
            Text = title.Length == 0 ? "Minister" : title, FontFamily = Ui, FontWeight = FontWeight.SemiBold, FontSize = 51, Foreground = Title,
            HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = W - 320,
        };
        var badge = new Border
        {
            Background = Red, CornerRadius = new CornerRadius(15), Padding = new Thickness(33, 12), HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock { Text = "TIME IS UP", FontFamily = Ui, FontWeight = FontWeight.ExtraBold, FontSize = 45, LetterSpacing = 9, Foreground = Brushes.White },
        };
        // Name and badge share one slot so the digits do not jump when time runs out.
        column.Children.Add(new Panel { Children = { name, badge } });

        var time = new TextBlock
        {
            FontFamily = Mono, FontWeight = FontWeight.Medium, FontSize = 440, LetterSpacing = -15,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -20, 0, -40),
        };
        // On a narrow (square or portrait) screen the digits shrink to fit.
        column.Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxWidth = W - 120, Child = time });

        var barWidth = Math.Min(1350, W * 0.8);
        var fill = new Border { Height = 24, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(12) };
        column.Children.Add(new Border
        {
            Width = barWidth, Height = 24, Background = Track, CornerRadius = new CornerRadius(12), ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 44, 0, 0), Child = fill,
        });

        var message = new TextBlock { FontFamily = Ui, FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 26, 0, 0) };
        column.Children.Add(message);

        var parts = new Parts
        {
            Root = frame, Show = t, Use24Hour = use24Hour, Frame = frame, Clock = clock, Label = label, Name = name, Badge = badge,
            Time = time, Fill = fill, FillWidth = barWidth, Message = message,
        };
        parts.Update(utcNow);
        return parts;
    }

    /// <summary>A coloured band across the top of the Stage Monitor; the words keep showing underneath.</summary>
    public static Parts Bar(MinisterTimerShow t, bool use24Hour, DateTime utcNow)
    {
        var band = new Border { Height = 180, Padding = new Thickness(60, 0) };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        band.Child = grid;
        var title = TitleOf(t);
        grid.Children.Add(new TextBlock
        {
            Text = title.Length == 0 ? "Minister" : title, FontFamily = Ui, FontWeight = FontWeight.ExtraBold, FontSize = 44, Foreground = Black,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var time = new TextBlock
        {
            FontFamily = Mono, FontWeight = FontWeight.Medium, FontSize = 140, LetterSpacing = -4, Foreground = Black,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(time, 1);
        grid.Children.Add(time);

        var fill = new Border { Height = 12, HorizontalAlignment = HorizontalAlignment.Left };
        var root = new StackPanel
        {
            Children =
            {
                band,
                new Border { Height = 12, Background = Track, Child = fill },
            },
        };
        var parts = new Parts { Root = root, Show = t, Use24Hour = use24Hour, Band = band, Time = time, Fill = fill, FillWidth = W };
        parts.Update(utcNow);
        return parts;
    }
}
