using ConvertApi.Worker;

namespace ConvertApi.UnitTests.Worker;

public sealed class WorkDirectoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "convertapi-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ScratchDirectoriesOlderThanAConversionCanRun_AreRemoved()
    {
        var stale = Scratch("job1-attempt1", Now.AddMinutes(-30));
        var running = Scratch("job2-attempt1", Now.AddMinutes(-2));
        var imageMagick = Scratch("imagemagick", Now.AddMinutes(-30));

        var removed = WorkDirectory.RemoveStale(_root, olderThan: TimeSpan.FromMinutes(10), Now);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(running));
        Assert.True(Directory.Exists(imageMagick));
    }

    [Fact]
    public void AMissingRoot_IsNotAnError()
    {
        Assert.Equal(0, WorkDirectory.RemoveStale(Path.Combine(_root, "absent"), TimeSpan.FromMinutes(10), Now));
    }

    private string Scratch(string name, DateTimeOffset lastWrite)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "input.mp4"), [1, 2, 3]);
        Directory.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return path;
    }
}
