namespace Historia.Tests;

/// <summary>
/// Guards that the pty I/O loops must run on dedicated background threads,
/// never the <see cref="System.Threading.ThreadPool"/>, and must exit promptly once their
/// streams close. See <see cref="PtyIoPump"/>.
/// </summary>
[Trait("tier", "unit")]
public class PtyIoPumpTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Read_RunsOffTheThreadPool()
    {
        var ran = new ManualResetEventSlim();
        var onPool = true;
        var output = new BlockingTestStream();
        output.OnRead = () => { onPool = Thread.CurrentThread.IsThreadPoolThread; ran.Set(); };
        var input = new BlockingTestStream();

        var pump = new PtyIoPump(output, input, _ => { }, null, "test");
        try
        {
            output.Push("hi"u8.ToArray());
            Assert.True(ran.Wait(Timeout));
            Assert.False(onPool, "the read loop must not run on a ThreadPool worker");
        }
        finally
        {
            output.CloseForRead();
            input.CloseForRead();
            pump.Dispose();
        }
    }

    [Fact]
    public void Write_DrainsOffTheThreadPoolInOrder()
    {
        var wrote = new ManualResetEventSlim();
        var onPool = true;
        var output = new BlockingTestStream();
        var input = new BlockingTestStream();
        input.OnWrite = () => { onPool = Thread.CurrentThread.IsThreadPoolThread; wrote.Set(); };

        var pump = new PtyIoPump(output, input, _ => { }, null, "test");
        try
        {
            pump.Write("abc"u8);
            Assert.True(wrote.Wait(Timeout));
            Assert.False(onPool, "the write loop must not run on a ThreadPool worker");
            Assert.True(SpinUntil(() => input.Written.Count >= 3));
            Assert.Equal("abc"u8.ToArray(), input.Written.Take(3).ToArray());
        }
        finally
        {
            output.CloseForRead();
            input.CloseForRead();
            pump.Dispose();
        }
    }

    [Fact]
    public void Read_ForwardsChunksInOrderThenRaisesEndedOnEof()
    {
        var chunks = new BlockingCollection<byte[]>();
        var ended = new ManualResetEventSlim();
        var output = new BlockingTestStream();
        var input = new BlockingTestStream();

        var pump = new PtyIoPump(output, input, m => chunks.Add(m.ToArray()), ended.Set, "test");
        try
        {
            output.Push("one"u8.ToArray());
            output.Push("two"u8.ToArray());
            output.CloseForRead();   // Read returns 0 → EOF

            Assert.True(ended.Wait(Timeout));
            Assert.Equal("one", System.Text.Encoding.UTF8.GetString(chunks.Take()));
            Assert.Equal("two", System.Text.Encoding.UTF8.GetString(chunks.Take()));
        }
        finally
        {
            input.CloseForRead();
            pump.Dispose();
        }
    }

    [Fact]
    public void Read_EndsWithoutSpinningWhenStreamThrows()
    {
        var output = new BlockingTestStream { ThrowOnRead = () => new IOException("pipe broke") };
        var input = new BlockingTestStream();
        var ended = new ManualResetEventSlim();

        var pump = new PtyIoPump(output, input, _ => { }, ended.Set, "test");
        try
        {
            Assert.True(ended.Wait(Timeout));
            Assert.True(output.ReadCallCount <= 2, $"expected the loop to stop, not spin (got {output.ReadCallCount} reads)");
        }
        finally
        {
            input.CloseForRead();
            pump.Dispose();
        }
    }

    [Fact]
    public void Dispose_ReturnsPromptlyAndJoinsBothThreadsOnceStreamsClose()
    {
        var output = new BlockingTestStream();
        var input = new BlockingTestStream();
        var pump = new PtyIoPump(output, input, _ => { }, null, "test");

        // Mirrors PortaPtyBackend.Connection.Dispose: close the handles first, then the pump.
        output.CloseForRead();
        input.CloseForRead();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        pump.Dispose();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Dispose blocked for {sw.Elapsed}");
        Assert.Equal(0, pump.LiveThreadCount);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var pump = new PtyIoPump(new BlockingTestStream(), new BlockingTestStream(), _ => { }, null, "test");
        pump.Dispose();
        pump.Dispose();
    }

    [Fact]
    public void Write_DropsRatherThanBlockingWhenThePtyIsNotDraining()
    {
        var output = new BlockingTestStream();
        var wedged = new BlockingTestStream { BlockWritesForever = true };
        var pump = new PtyIoPump(output, wedged, _ => { }, null, "test");
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 5000; i++)
                pump.Write("x"u8);
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
                $"Write must never block the caller even against a wedged pty (took {sw.Elapsed})");
        }
        finally
        {
            output.CloseForRead();
            wedged.CloseForRead();   // releases the wedged write
            pump.Dispose();
        }
    }

    private static bool SpinUntil(Func<bool> condition)
        => SpinWait.SpinUntil(condition, Timeout);

    // ── a Stream whose Read/Write can be blocked, fed, closed, or made to throw ───────────────

    private sealed class BlockingTestStream : Stream
    {
        private readonly BlockingCollection<byte[]> _pending = new();
        private readonly List<byte> _written = [];
        private volatile bool _closed;

        public Action? OnRead;
        public Action? OnWrite;
        public Func<Exception>? ThrowOnRead;
        public bool BlockWritesForever;

        public int ReadCallCount;
        public IReadOnlyList<byte> Written { get { lock (_written) return _written.ToArray(); } }

        public void Push(byte[] data) => _pending.Add(data);

        public void CloseForRead()
        {
            _closed = true;
            try { _pending.CompleteAdding(); } catch { /* already completed */ }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref ReadCallCount);
            OnRead?.Invoke();
            if (ThrowOnRead is not null)
                throw ThrowOnRead();

            try
            {
                if (_pending.TryTake(out var chunk, System.Threading.Timeout.Infinite))
                {
                    var n = Math.Min(count, chunk.Length);
                    Array.Copy(chunk, 0, buffer, offset, n);
                    return n;
                }
            }
            catch (InvalidOperationException)
            {
                // completed while we waited
            }
            return 0;   // EOF
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            OnWrite?.Invoke();
            while (BlockWritesForever && !_closed)
                Thread.Sleep(10);
            lock (_written)
                _written.AddRange(new ReadOnlySpan<byte>(buffer, offset, count).ToArray());
        }

        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            CloseForRead();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
