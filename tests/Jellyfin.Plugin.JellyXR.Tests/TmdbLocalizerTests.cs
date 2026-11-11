using Xunit;

namespace Jellyfin.Plugin.JellyXR.Tests;

public class TmdbLocalizerTests
{
    [Theory]
    [InlineData("he", "he")]
    [InlineData(" pt_BR ", "pt-BR")]
    [InlineData("", "")]
    [InlineData("x", "")]
    public void Normalize_gives_tmdb_tag(string input, string expected)
        => Assert.Equal(expected, TmdbLocalizer.Normalize(input));

    [Fact]
    public void Merge_prefers_requested_language_then_english()
    {
        var wanted = new Dictionary<int, LocalizedCredit> { [1] = new("שם", null), [2] = new(null, "דמות") };
        var english = new Dictionary<int, LocalizedCredit> { [1] = new("Name", "Role"), [2] = new("Other", "Part"), [3] = new("Solo", null) };

        var m = TmdbLocalizer.Merge(wanted, english);

        Assert.Equal(new LocalizedCredit("שם", "Role"), m[1]);
        Assert.Equal(new LocalizedCredit("Other", "דמות"), m[2]);
        Assert.Equal(new LocalizedCredit("Solo", null), m[3]);
    }

    [Fact]
    public void PeopleAround_dedupes_by_tmdb_id_across_spellings()
    {
        var tl = new XrayarrTimeline
        {
            Segments =
            {
                new XrayarrSegment { StartMs = 0, EndMs = 1000, Cast = { new XrayarrPerson { Name = "Jon Doe", TmdbPersonId = 5 } } },
                new XrayarrSegment { StartMs = 1000, EndMs = 2000, Cast = { new XrayarrPerson { Name = "John Doe", TmdbPersonId = 5 } } },
            },
        };

        Assert.Single(tl.PeopleAround(500, 0, 2000));
    }
}
