using Foxfire.Server.Storage;

namespace Foxfire.UnitTests.Storage;

public sealed class LocalMediaStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "foxfire-unit", Guid.NewGuid().ToString("N"));
    private readonly LocalMediaStorage _storage;

    public LocalMediaStorageTests() => _storage = new LocalMediaStorage(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string PublishSample(Guid id, string playlist = "#EXTM3U")
    {
        var work = _storage.CreateWorkDirectory(id, 1);
        File.WriteAllText(Path.Combine(work, "index.m3u8"), playlist);
        File.WriteAllBytes(Path.Combine(work, "init.mp4"), new byte[10]);
        File.WriteAllBytes(Path.Combine(work, "seg_00000.m4s"), new byte[100]);
        File.WriteAllBytes(Path.Combine(work, "poster.jpg"), new byte[5]);
        _storage.Publish(id, work);
        return work;
    }

    [Theory]
    [InlineData("index.m3u8")]
    [InlineData("init.mp4")]
    [InlineData("seg_00000.m4s")]
    [InlineData("seg_12345.m4s")]
    [InlineData("poster.jpg")]
    public void Allowed_asset_names(string name) => Assert.True(LocalMediaStorage.IsAllowedAssetName(name));

    [Theory]
    [InlineData("../index.m3u8")]
    [InlineData("..\\index.m3u8")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("seg_1.m4s")]
    [InlineData("seg_00000.m4s/../../x")]
    [InlineData("source.mp4")]
    [InlineData("INDEX.M3U8")]
    [InlineData("index.m3u8 ")]
    [InlineData("index.m3u8\0")]
    [InlineData("")]
    public void Disallowed_asset_names_never_resolve(string name)
    {
        var id = Guid.NewGuid();
        PublishSample(id);

        Assert.False(LocalMediaStorage.IsAllowedAssetName(name));
        Assert.Null(_storage.GetAssetPath(id, name));
    }

    [Fact]
    public void Published_assets_resolve_inside_the_video_directory_only()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        PublishSample(a);

        var path = _storage.GetAssetPath(a, "index.m3u8");

        Assert.NotNull(path);
        Assert.StartsWith(Path.Combine(_storage.Root, "videos", a.ToString("N")), path, StringComparison.OrdinalIgnoreCase);
        Assert.Null(_storage.GetAssetPath(b, "index.m3u8"));
        Assert.Null(_storage.GetAssetPath(a, "seg_00001.m4s")); // allowed name, but absent
    }

    [Fact]
    public void Sources_are_never_servable()
    {
        var id = Guid.NewGuid();
        var upload = Path.Combine(_storage.UploadsDirectory, "u1");
        File.WriteAllBytes(upload, new byte[42]);

        var source = _storage.AdoptSource(id, upload, ".MKV");

        Assert.EndsWith("source.mkv", source, StringComparison.Ordinal);
        Assert.Equal(source, _storage.FindSource(id));
        Assert.False(File.Exists(upload));
        Assert.Null(_storage.GetAssetPath(id, "source.mkv"));
    }

    [Fact]
    public void Odd_extensions_are_neutralized()
    {
        var upload = Path.Combine(_storage.UploadsDirectory, "u2");
        File.WriteAllBytes(upload, [1]);

        var source = _storage.AdoptSource(Guid.NewGuid(), upload, "../../evil");

        Assert.EndsWith("source.bin", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_replaces_the_previous_output_and_consumes_the_work_directory()
    {
        var id = Guid.NewGuid();
        PublishSample(id, "first");

        var work = PublishSample(id, "second");

        Assert.Equal("second", File.ReadAllText(_storage.GetAssetPath(id, "index.m3u8")!));
        Assert.False(Directory.Exists(work));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_storage.Root, "work")));
    }

    [Fact]
    public void Publish_refuses_directories_outside_work()
    {
        var outside = Path.Combine(_root, "videos");

        Assert.Throws<InvalidOperationException>(() => _storage.Publish(Guid.NewGuid(), outside));
    }

    [Fact]
    public void Measure_counts_output_and_source_and_delete_removes_everything()
    {
        var id = Guid.NewGuid();
        PublishSample(id);
        var upload = Path.Combine(_storage.UploadsDirectory, "u3");
        File.WriteAllBytes(upload, new byte[1000]);
        _storage.AdoptSource(id, upload, ".mp4");
        _storage.CreateWorkDirectory(id, 2);

        var footprint = _storage.Measure(id);
        Assert.Equal(7 + 10 + 100 + 5, footprint.OutputBytes);
        Assert.Equal(1000, footprint.SourceBytes);

        _storage.Delete(id);

        Assert.Equal(0, _storage.Measure(id).Total);
        Assert.Null(_storage.FindSource(id));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_storage.Root, "work")));
    }

    [Fact]
    public void CleanWorkDirectories_keeps_only_the_requested_videos()
    {
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        var kept = _storage.CreateWorkDirectory(keep, 1);
        var dropped = _storage.CreateWorkDirectory(drop, 1);

        _storage.CleanWorkDirectories(new HashSet<Guid> { keep });

        Assert.True(Directory.Exists(kept));
        Assert.False(Directory.Exists(dropped));
    }

    [Fact]
    public void Lists_published_and_source_ids()
    {
        var id = Guid.NewGuid();
        PublishSample(id);
        Directory.CreateDirectory(Path.Combine(_root, "videos", "not-a-guid"));

        Assert.Equal([id], _storage.ListPublishedVideoIds());
        Assert.Empty(_storage.ListSourceVideoIds());
    }
}
