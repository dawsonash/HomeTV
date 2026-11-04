using HomeTV.Core.Photos;

namespace HomeTV.Tests;

public sealed class PhotoSortingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hometv-sorting-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData(PhotoMode.All, null, true)]
    [InlineData(PhotoMode.People, null, false)]
    [InlineData(PhotoMode.SafeForWork, null, false)]
    public void Untagged_photos_only_show_in_All(PhotoMode mode, PhotoTags? tags, bool expected) =>
        Assert.Equal(expected, PhotoFilter.Matches(mode, tags));

    [Fact]
    public void Modes_follow_the_tags()
    {
        var beach = new PhotoTags(People: true, Nature: true, SafeForWork: true, Manual: false, Version: "1");
        var party = new PhotoTags(People: true, Nature: false, SafeForWork: false, Manual: false, Version: "1");
        Assert.True(PhotoFilter.Matches(PhotoMode.Nature, beach));
        Assert.True(PhotoFilter.Matches(PhotoMode.People, beach));
        Assert.False(PhotoFilter.Matches(PhotoMode.Nature, party));
        Assert.False(PhotoFilter.Matches(PhotoMode.SafeForWork, party));
        Assert.True(PhotoFilter.Matches(PhotoMode.All, party));
    }

    [Fact]
    public void Settings_default_to_all_and_30_seconds_and_persist()
    {
        var path = Path.Combine(_dir, "photo-settings.json");
        Assert.Equal(new PhotoSettings(PhotoMode.All, 30), new PhotoSettingsStore(path).Current);

        new PhotoSettingsStore(path).Update(s => s with { Mode = PhotoMode.Nature, SlideSeconds = 90 });
        Assert.Equal(new PhotoSettings(PhotoMode.Nature, 90), new PhotoSettingsStore(path).Current);
    }

    [Fact]
    public void Slide_time_is_clamped()
    {
        var store = new PhotoSettingsStore(Path.Combine(_dir, "photo-settings.json"));
        Assert.Equal(PhotoSettings.MinSlideSeconds, store.Update(s => s with { SlideSeconds = 0 }).SlideSeconds);
        Assert.Equal(PhotoSettings.MaxSlideSeconds, store.Update(s => s with { SlideSeconds = 99_999 }).SlideSeconds);
    }

    [Fact]
    public void Tags_persist_beside_the_photos_and_are_pruned_with_them()
    {
        var store = new PhotoTagStore(_dir);
        store.Set("a.jpg", new PhotoTags(true, false, true, false, "v1"));
        store.Set("b.jpg", new PhotoTags(false, true, true, true, "v1"));
        // Rewrites over the hidden file on Windows.
        store.Set("b.jpg", new PhotoTags(false, true, false, true, "v1"));

        var reloaded = new PhotoTagStore(_dir);
        Assert.Equal(new PhotoTags(false, true, false, true, "v1"), reloaded.Get("B.JPG"));
        Assert.True(reloaded.Prune(["a.jpg"]));
        Assert.Null(new PhotoTagStore(_dir).Get("b.jpg"));
        Assert.NotNull(new PhotoTagStore(_dir).Get("a.jpg"));
    }

    [Fact]
    public void The_tag_file_is_not_listed_as_a_photo()
    {
        new PhotoTagStore(_dir).Set("a.jpg", new PhotoTags(true, false, true, false, "v1"));
        Assert.Empty(new PhotoLibrary(_dir).List());
    }
}
