using Xunit;

namespace Jellyfin.Plugin.JellyXR.Tests;

public class PosterBadgerTests
{
    [Theory]
    [InlineData(1000, 1500)] // poster
    [InlineData(1920, 1080)] // episode thumbnail
    [InlineData(300, 450)]
    public void Place_TopLeft_EvenSizes_InsideImage(int w, int h)
    {
        var box = Assert.NotNull(PosterBadger.Place(w, h));
        Assert.Equal(0, box.Width % 2);
        Assert.Equal(0, box.Height % 2);
        Assert.InRange(box.Width / (double)box.Height, 2.2, 2.8);
        Assert.True(box.X > 0 && box.Y > 0);
        Assert.True(box.X + box.Width <= w * 0.7);
        Assert.True(box.Y + box.Height <= h * 0.3);
    }

    [Fact]
    public void Place_PosterAndThumb_SimilarWeight()
    {
        var poster = PosterBadger.Place(1000, 1500)!.Value;
        var thumb = PosterBadger.Place(1500, 1000)!.Value;
        Assert.Equal(poster.Height, thumb.Height);
    }

    [Fact]
    public void Place_NarrowImage_ClampedToWidth()
    {
        var box = PosterBadger.Place(200, 2000)!.Value;
        Assert.True(box.Width <= 120);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 800)]
    public void Place_UnknownOrTiny_Null(int w, int h) => Assert.Null(PosterBadger.Place(w, h));

    [Fact]
    public void FfmpegArgs_OverlayScaledBadge_SingleJpeg()
    {
        var args = PosterBadger.FfmpegArgs("in file.png", "badge.png", "out.jpg", new BadgeBox(260, 104, 36, 36));
        Assert.Equal("out.jpg", args[^1]);
        Assert.Contains("in file.png", args); // passed as one argument, no quoting needed
        Assert.Contains("[1:v]scale=260:104[b];[0:v][b]overlay=36:36", args);
        Assert.Equal("1", args[args.ToList().IndexOf("-frames:v") + 1]);
    }

    [Fact]
    public void IsUnder_OnlyPathsInsideTheDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jellyxr", "badges");
        Assert.True(PosterBadger.IsUnder(Path.Combine(dir, "a.jpg"), dir));
        Assert.False(PosterBadger.IsUnder(Path.Combine(dir + "2", "a.jpg"), dir));
        Assert.False(PosterBadger.IsUnder(Path.Combine(Path.GetTempPath(), "poster.jpg"), dir));
        Assert.False(PosterBadger.IsUnder(null, dir));
    }
}
