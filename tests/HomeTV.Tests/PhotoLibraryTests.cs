using HomeTV.Core.Photos;

namespace HomeTV.Tests;

public sealed class PhotoLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hometv-photos-" + Guid.NewGuid().ToString("n"));
    private readonly string _folder;

    public PhotoLibraryTests()
    {
        _folder = Path.Combine(_root, "photos");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Touch(string relative) => File.WriteAllBytes(Path.Combine(_folder, relative), [1, 2, 3]);

    [Fact]
    public void Lists_videos_too_marked_as_videos()
    {
        Touch("a.jpg");
        Touch("clip.MP4");
        Touch("b.mov");

        var listed = new PhotoLibrary(_folder).List();
        Assert.Equal(["a.jpg", "b.mov", "clip.MP4"], listed.Select(p => p.Name));
        Assert.Equal([PhotoKind.Image, PhotoKind.Video, PhotoKind.Video], listed.Select(p => p.Kind));
    }

    [Fact]
    public async Task Saves_an_upload_without_replacing_a_file_already_there()
    {
        Touch("beach.jpg");
        var library = new PhotoLibrary(_folder);

        var saved = await library.SaveAsync("beach.JPG", new MemoryStream([9, 9]));

        Assert.Equal("beach (2).jpg", saved.Name);
        Assert.Equal([9, 9], File.ReadAllBytes(Path.Combine(_folder, "beach (2).jpg")));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(_folder, "beach.jpg")));
    }

    [Theory]
    [InlineData("../../evil.jpg", "evil.jpg")]
    [InlineData("C:\\Windows\\evil.png", "evil.png")]
    [InlineData("..hidden.mp4", "hidden.mp4")]
    [InlineData("a:b?.jpg", "a_b_.jpg")]
    public async Task Upload_names_stay_inside_the_folder(string sent, string expected)
    {
        var saved = await new PhotoLibrary(_folder).SaveAsync(sent, new MemoryStream([1]));

        Assert.Equal(expected, saved.Name);
        Assert.True(File.Exists(Path.Combine(_folder, expected)));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("IMG_0001.HEIC")]
    [InlineData("noextension")]
    public async Task Refuses_uploads_the_slideshow_cant_show(string name)
    {
        await Assert.ThrowsAsync<PhotoRejectedException>(() => new PhotoLibrary(_folder).SaveAsync(name, new MemoryStream([1])));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    private sealed class FakeHeicConverter : IHeicConverter
    {
        public Task ConvertToJpegAsync(string heicPath, string jpegPath, CancellationToken ct = default)
        {
            if (File.ReadAllBytes(heicPath) is [0])
                throw new PhotoRejectedException("unreadable");
            File.WriteAllBytes(jpegPath, [0xFF, 0xD8]);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task HEIC_uploads_are_converted_to_JPEG()
    {
        Touch("IMG_0001.jpg");
        var library = new PhotoLibrary(_folder, new FakeHeicConverter());

        var saved = await library.SaveAsync("IMG_0001.HEIC", new MemoryStream([1, 2]));

        Assert.Equal("IMG_0001 (2).jpg", saved.Name);
        Assert.Equal(PhotoKind.Image, saved.Kind);
        Assert.Equal([0xFF, 0xD8], File.ReadAllBytes(Path.Combine(_folder, saved.Name)));
        Assert.Equal(["IMG_0001 (2).jpg", "IMG_0001.jpg"], Directory.GetFiles(_folder).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task An_unreadable_HEIC_leaves_nothing_behind()
    {
        var library = new PhotoLibrary(_folder, new FakeHeicConverter());

        await Assert.ThrowsAsync<PhotoRejectedException>(() => library.SaveAsync("bad.heic", new MemoryStream([0])));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task An_empty_upload_leaves_nothing_behind()
    {
        await Assert.ThrowsAsync<PhotoRejectedException>(() => new PhotoLibrary(_folder).SaveAsync("a.jpg", new MemoryStream()));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void Deletes_only_photos_in_the_folder()
    {
        Touch("a.jpg");
        Touch("notes.txt");
        var library = new PhotoLibrary(_folder);

        Assert.True(library.Delete("a.jpg"));
        Assert.False(library.Delete("notes.txt"));
        Assert.False(library.Delete("../a.jpg"));
        Assert.Equal(["notes.txt"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void Lists_photos_and_videos_only_sorted_by_name()
    {
        Touch("b.JPG");
        Touch("a.png");
        Touch("notes.txt");
        Touch("c.heic");
        Directory.CreateDirectory(Path.Combine(_folder, "sub"));
        Touch(Path.Combine("sub", "d.jpg"));

        Assert.Equal(["a.png", "b.JPG"], new PhotoLibrary(_folder).List().Select(p => p.Name));
    }

    [Fact]
    public void Missing_folder_is_empty_until_created()
    {
        var library = new PhotoLibrary(Path.Combine(_root, "not-yet"));
        Assert.Empty(library.List());

        library.EnsureFolder();
        Assert.True(Directory.Exists(library.Folder));
    }

    [Fact]
    public void Version_changes_when_the_file_does()
    {
        Touch("a.jpg");
        var library = new PhotoLibrary(_folder);
        var before = library.List().Single().Version;

        File.WriteAllBytes(Path.Combine(_folder, "a.jpg"), [1, 2, 3, 4]);

        Assert.NotEqual(before, library.List().Single().Version);
    }

    [Fact]
    public void Resolves_photos_in_the_folder()
    {
        Touch("a.jpg");
        Assert.Equal(Path.Combine(_folder, "a.jpg"), new PhotoLibrary(_folder).Resolve("a.jpg"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("missing.jpg")]
    [InlineData("notes.txt")]
    [InlineData("../outside.jpg")]
    [InlineData("..\\outside.jpg")]
    [InlineData("sub/d.jpg")]
    [InlineData("a.jpg:stream")]
    public void Refuses_anything_else(string name)
    {
        Touch("a.jpg");
        Touch("notes.txt");
        File.WriteAllBytes(Path.Combine(_root, "outside.jpg"), [1]);
        Directory.CreateDirectory(Path.Combine(_folder, "sub"));
        Touch(Path.Combine("sub", "d.jpg"));

        Assert.Null(new PhotoLibrary(_folder).Resolve(name));
    }
}
