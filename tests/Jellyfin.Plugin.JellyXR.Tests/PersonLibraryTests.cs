using Jellyfin.Plugin.JellyXR.Overlay;
using Xunit;

namespace Jellyfin.Plugin.JellyXR.Tests;

public class PersonLibraryTests
{
    private static LibraryTitle Movie(string name, DateTime? premiere = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), name, premiere?.Year, "Movie", TimeSpan.FromMinutes(100).Ticks, null, "o", premiere);

    private static LibraryTitle Series(string name, Guid id, DateTime? premiere = null) =>
        new(id, name, premiere?.Year, "Series", null, 2, "o", premiere);

    [Fact]
    public void Rollup_EpisodesBecomeOneSeries()
    {
        var sid = Guid.NewGuid();
        var s = Series("Show", sid, new DateTime(2015, 1, 1));
        var eps = new[] { (sid, s), (sid, s), (sid, s) };

        var r = PersonLibrary.Rollup(Array.Empty<LibraryTitle>(), eps, new[] { s }, Guid.NewGuid(), null);

        Assert.Single(r);
        Assert.Equal(sid, r[0].Id);
        Assert.Equal("Series", r[0].Kind);
    }

    [Fact]
    public void Rollup_ExcludesCurrentMovieAndCurrentSeries()
    {
        var current = Movie("Now playing");
        var other = Movie("Other");
        var sid = Guid.NewGuid();
        var s = Series("Current show", sid);
        var otherSid = Guid.NewGuid();
        var s2 = Series("Other show", otherSid);

        var byMovie = PersonLibrary.Rollup(new[] { current, other }, Array.Empty<(Guid, LibraryTitle)>(), Array.Empty<LibraryTitle>(), current.Id, null);
        Assert.Equal(new[] { "Other" }, byMovie.Select(t => t.Name));

        var bySeries = PersonLibrary.Rollup(new[] { other }, new[] { (sid, s), (otherSid, s2) }, new[] { s }, Guid.NewGuid(), sid);
        Assert.DoesNotContain(bySeries, t => t.Id == sid);
        Assert.Contains(bySeries, t => t.Id == otherSid);
        Assert.Contains(bySeries, t => t.Id == other.Id);
    }

    [Fact]
    public void Rollup_NewestFirst_NullsLast()
    {
        var old = Movie("Old", new DateTime(1999, 5, 1));
        var none = Movie("Undated");
        var recent = Movie("Recent", new DateTime(2021, 3, 1));
        var sid = Guid.NewGuid();
        var mid = Series("Mid", sid, new DateTime(2010, 1, 1));

        var r = PersonLibrary.Rollup(new[] { old, none, recent }, new[] { (sid, mid) }, Array.Empty<LibraryTitle>(), Guid.NewGuid(), null);

        Assert.Equal(new[] { "Recent", "Mid", "Old", "Undated" }, r.Select(t => t.Name));
    }

    [Fact]
    public void ForUser_ScopesTheQueryToTheUser()
    {
        var user = new Jellyfin.Data.Entities.User("ada", "auth", "reset") { MaxParentalAgeRating = 12 };
        var q = new MediaBrowser.Controller.Entities.InternalItemsQuery().ForUser(user);

        Assert.Same(user, q.User);
        Assert.Equal(12, q.MaxParentalRating); // SetUser ran: parental rating applies
        Assert.Throws<ArgumentNullException>(() => new MediaBrowser.Controller.Entities.InternalItemsQuery().ForUser(null!));
        Assert.Throws<MissingMemberException>(() => new MediaBrowser.Controller.Entities.InternalItemsQuery().ForUser("not a user"));
    }
}
