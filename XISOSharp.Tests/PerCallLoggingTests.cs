using XISOSharp.Logging;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// LIB-02: per-call log scopes route to independent sinks, keep the static
/// Logger working when no scope is active, and never let a failing sink break
/// an operation.
/// </summary>
[Collection("Sequential")]
public sealed class PerCallLoggingTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly TextWriter _savedOut;
    private readonly TextWriter _savedError;
    private readonly bool _savedQuiet;
    private readonly bool _savedRealQuiet;

    public PerCallLoggingTests()
    {
        _savedOut = Logger.Out;
        _savedError = Logger.Error;
        _savedQuiet = Logger.Quiet;
        _savedRealQuiet = Logger.RealQuiet;
    }

    public void Dispose()
    {
        Logger.Out = _savedOut;
        Logger.Error = _savedError;
        Logger.Quiet = _savedQuiet;
        Logger.RealQuiet = _savedRealQuiet;
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    [Fact]
    public void CreateWithSink_DoesNotWriteToStaticOutput()
    {
        string work = CreateTempDir();
        MemoryFilesystem source = new();
        AddText(source, "src/a.txt", "a");
        AddText(source, "src/sub/b.txt", "b");

        StringWriter staticOut = new();
        Logger.Out = staticOut;
        Logger.Error = staticOut;
        Logger.Quiet = false;
        Logger.RealQuiet = false;

        RecordingSink sink = new();
        int result = XisoWriter.PackFromDirectory(source, "src", Path.Combine(work, "out.iso"),
            fileTime: 0, log: new XisoLogOptions { Sink = sink });

        Assert.Equal(0, result);
        Assert.NotEmpty(sink.Info);
        Assert.Contains(sink.Info, static m => m.Contains("a.txt", StringComparison.Ordinal));
        Assert.Empty(sink.Warnings);
        Assert.Empty(sink.Errors);
        Assert.Equal(string.Empty, staticOut.ToString());
    }

    [Fact]
    public async Task ConcurrentCreates_LogToSeparateSinks()
    {
        string work = CreateTempDir();
        MemoryFilesystem first = new();
        AddText(first, "one/only-first.txt", "first");
        MemoryFilesystem second = new();
        AddText(second, "two/only-second.txt", "second");

        StringWriter staticOut = new();
        Logger.Out = staticOut;
        Logger.Error = staticOut;
        Logger.Quiet = false;
        Logger.RealQuiet = false;

        RecordingSink firstSink = new();
        RecordingSink secondSink = new();
        Task<int>[] jobs =
        [
            XisoWriter.PackFromDirectoryAsync(first, "one", Path.Combine(work, "one.iso"),
                fileTime: 0, log: new XisoLogOptions { Sink = firstSink }),
            XisoWriter.PackFromDirectoryAsync(second, "two", Path.Combine(work, "two.iso"),
                fileTime: 0, log: new XisoLogOptions { Sink = secondSink }),
        ];

        int[] results = await Task.WhenAll(jobs);

        Assert.All(results, static r => Assert.Equal(0, r));
        Assert.Contains(firstSink.Info, static m => m.Contains("only-first.txt", StringComparison.Ordinal));
        Assert.Contains(secondSink.Info, static m => m.Contains("only-second.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(firstSink.Info, static m => m.Contains("only-second.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(secondSink.Info, static m => m.Contains("only-first.txt", StringComparison.Ordinal));
        Assert.Equal(string.Empty, staticOut.ToString());
    }

    [Fact]
    public void ExtractWithSink_RoutesOutput()
    {
        string work = CreateTempDir();
        string sourceDir = Path.Combine(work, "src");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "hello.txt"), "hello");
        string iso = Path.Combine(work, "in.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(sourceDir, iso, fileTime: 0));

        StringWriter staticOut = new();
        Logger.Out = staticOut;
        Logger.Error = staticOut;
        Logger.Quiet = false;
        Logger.RealQuiet = false;

        RecordingSink sink = new();
        MemoryFilesystem destination = new();
        UnpackOptions options = new() { Log = new XisoLogOptions { Sink = sink } };

        int result = XisoReader.UnpackImage(iso, destination, options: options);

        Assert.Equal(0, result);
        Assert.NotEmpty(sink.Info);
        Assert.Contains(sink.Info, static m => m.Contains("hello.txt", StringComparison.Ordinal));
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(destination.ReadAllBytes("hello.txt")));
        Assert.Equal(string.Empty, staticOut.ToString());
    }

    [Fact]
    public void StaticLogger_StillWorksWithoutScope()
    {
        StringWriter staticOut = new();
        Logger.Out = staticOut;
        Logger.Quiet = false;
        Logger.RealQuiet = false;

        Logger.LogLine("static message");

        Assert.Contains("static message", staticOut.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SuppressStaticOutputFalse_WritesBothSinks()
    {
        StringWriter staticOut = new();
        Logger.Out = staticOut;
        Logger.Quiet = false;
        Logger.RealQuiet = false;

        RecordingSink sink = new();
        using (XisoLogScope.Begin(new XisoLogOptions { Sink = sink, SuppressStaticOutput = false }))
        {
            Logger.LogLine("both");
        }

        Assert.Contains("both", sink.Info, StringComparer.Ordinal);
        Assert.Contains("both", staticOut.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NestedScopes_RestoreOuterSink()
    {
        RecordingSink outer = new();
        RecordingSink inner = new();

        using (XisoLogScope.Begin(new XisoLogOptions { Sink = outer }))
        {
            Logger.LogDebug("outer-before");
            using (XisoLogScope.Begin(new XisoLogOptions { Sink = inner }))
            {
                Logger.LogDebug("inner");
            }

            Logger.LogDebug("outer-after");
        }

        Assert.Contains("outer-before", outer.Debug, StringComparer.Ordinal);
        Assert.Contains("outer-after", outer.Debug, StringComparer.Ordinal);
        Assert.DoesNotContain("inner", outer.Debug, StringComparer.Ordinal);
        Assert.Contains("inner", inner.Debug, StringComparer.Ordinal);
    }

    [Fact]
    public void ThrowingSink_DoesNotBreakTheOperation()
    {
        string work = CreateTempDir();
        MemoryFilesystem source = new();
        AddText(source, "src/a.txt", "a");

        int result = XisoWriter.PackFromDirectory(source, "src", Path.Combine(work, "out.iso"),
            fileTime: 0, log: new XisoLogOptions { Sink = new ThrowingSink() });

        Assert.Equal(0, result);
        Assert.True(File.Exists(Path.Combine(work, "out.iso")));
    }

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_log_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void AddText(MemoryFilesystem filesystem, string path, string text)
    {
        using Stream stream = filesystem.CreateFile(path);
        using StreamWriter writer = new(stream);
        writer.Write(text);
    }

    private sealed class RecordingSink : IXisoLogger
    {
        internal List<string> Info { get; } = [];
        internal List<string> Warnings { get; } = [];
        internal List<string> Errors { get; } = [];
        internal List<string> Debug { get; } = [];

        public void Log(string message) => Info.Add(message);

        public void LogWarning(string message) => Warnings.Add(message);

        public void LogError(string message) => Errors.Add(message);

        public void LogDebug(string message) => Debug.Add(message);
    }

    private sealed class ThrowingSink : IXisoLogger
    {
        public void Log(string message) => throw new InvalidOperationException("sink failed");

        public void LogWarning(string message) => throw new InvalidOperationException("sink failed");

        public void LogError(string message) => throw new InvalidOperationException("sink failed");

        public void LogDebug(string message) => throw new InvalidOperationException("sink failed");

        public void Flush() => throw new InvalidOperationException("sink failed");
    }
}
