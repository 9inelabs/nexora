using System.Text.Json;
using Nexora.Core.Model;
using Nexora.Core.Protocol;
using Xunit;

namespace Nexora.Tests;

public class ScreenShapeTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1080)]      // not set: 16:9
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(512, 512, 1080, 1080)]  // a square LED wall
    [InlineData(1440, 1080, 1440, 1080)]
    [InlineData(1080, 1920, 1080, 1920)] // portrait: the shorter side stays 1080
    [InlineData(3840, 1080, 3840, 1080)]
    public void Canvas_keeps_the_shorter_side_at_1080(int pw, int ph, double w, double h)
    {
        var s = new ScreenInfo("main", "Main", ScreenKind.Audience, 1, true, 0, PixelWidth: pw, PixelHeight: ph);
        Assert.Equal((w, h), s.Canvas());
    }

    [Fact]
    public void Shape_and_area_survive_the_network_and_older_screens_still_load()
    {
        var s = new ScreenInfo("main", "LED Wall", ScreenKind.Audience, 2, true, 0, 768, 768, 0, 0, 768, 768, TestPattern: true, StretchToFill: false);
        var back = JsonSerializer.Deserialize<ScreenInfo>(JsonSerializer.Serialize(s, Wire.Options), Wire.Options);
        Assert.Equal(s, back);
        Assert.True(back!.HasArea);

        // Saved by an older version: no shape fields at all.
        var old = JsonSerializer.Deserialize<ScreenInfo>("""{"id":"main","name":"Main","kind":"Audience","display":1,"oneTextSource":true,"order":0}""", Wire.Options);
        Assert.Equal((1920.0, 1080.0), old!.Canvas());
        Assert.False(old.HasArea);
    }
}
