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
    public void Lists_images_only_sorted_by_name()
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
