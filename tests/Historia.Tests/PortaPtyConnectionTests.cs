namespace Historia.Tests;

/// <summary>
/// Guards that output a shell writes before anyone has subscribed to
/// <see cref="IPtyConnection.OutputReceived"/> is still delivered. The pty's read thread starts as
/// soon as the shell spawns, but <see cref="TerminalSession"/> subscribes only after the backend
/// returns, and the shell's first prompt routinely lands in that gap — when it was raised into an
/// empty event the terminal stayed blank forever, with no error anywhere.
/// </summary>
[Trait("tier", "unit")]
public class PortaPtyConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void OutputReadBeforeTheFirstSubscriber_IsDeliveredToIt()
    {
        var inner = new FakeInnerPty();
        using var connection = new PortaPtyBackend.Connection(inner);

        inner.Output.Push("PS C:\\> "u8.ToArray());
        inner.Output.WaitUntilConsumed();

        var received = new BlockingCollection<byte[]>();
        connection.OutputReceived += chunk => received.Add(chunk.ToArray());

        Assert.True(received.TryTake(out var first, Timeout), "the prompt read before subscribing was lost");
        Assert.Equal("PS C:\\> ", Encoding.UTF8.GetString(first));
    }

    [Fact]
    public void ChunksReadBeforeAndAfterSubscribing_ArriveInOrder()
    {
        var inner = new FakeInnerPty();
        using var connection = new PortaPtyBackend.Connection(inner);

        inner.Output.Push("one"u8.ToArray());
        inner.Output.WaitUntilConsumed();

        var received = new BlockingCollection<byte[]>();
        connection.OutputReceived += chunk => received.Add(chunk.ToArray());
        inner.Output.Push("two"u8.ToArray());

        Assert.True(received.TryTake(out var first, Timeout));
        Assert.True(received.TryTake(out var second, Timeout));
        Assert.Equal("one", Encoding.UTF8.GetString(first));
        Assert.Equal("two", Encoding.UTF8.GetString(second));
    }

    [Fact]
    public void Session_ShowsThePromptTheShellPrintedBeforeItAttached()
    {
        var inner = new FakeInnerPty();
        var connection = new PortaPtyBackend.Connection(inner);

        inner.Output.Push("PS C:\\> "u8.ToArray());
        inner.Output.WaitUntilConsumed();

        using var session = new TerminalSession(
            TerminalOrigin.Home("/home/me"), connection, new ImmediateUiDispatcher(), 80, 24, 1000);

        Assert.True(
            SpinWait.SpinUntil(() => session.ReadBuffer(v => v.GetLineText(0)) == "PS C:\\>", Timeout),
            "the emulator buffer never received the shell's first prompt");
    }

    [Fact]
    public void Dispose_WithNoSubscriber_ReleasesTheHeldReader()
    {
        var inner = new FakeInnerPty();
        var connection = new PortaPtyBackend.Connection(inner);

        inner.Output.Push("unclaimed"u8.ToArray());
        inner.Output.WaitUntilConsumed();
        inner.Output.Push("more"u8.ToArray());

        connection.Dispose();

        Assert.True(inner.DrainedBeforeClose,
            "a reader held for a subscriber must go back to draining the pipe once disposed");
    }

    /// <summary>
    /// A Porta.Pty connection whose reader stream blocks until the test pushes bytes. Its
    /// <see cref="Dispose"/> waits for pushed output to be read before closing, the way closing a
    /// pseudoconsole can wait for its final frame to be drained from the output pipe.
    /// </summary>
    private sealed class FakeInnerPty : PortaPty.IPtyConnection
    {
        public QueueStream Output { get; } = new();
        public QueueStream Input { get; } = new();
        public bool DrainedBeforeClose { get; private set; }

        public Stream ReaderStream => Output;
        public Stream WriterStream => Input;
        public int Pid => 0;
        public int ExitCode => 0;

        public event EventHandler<PortaPty.PtyExitedEventArgs>? ProcessExited { add { } remove { } }

        public void Kill() { }
        public void Resize(int cols, int rows) { }
        public bool WaitForExit(int milliseconds) => true;

        public void Dispose()
        {
            DrainedBeforeClose = Output.WaitUntilDrained(Timeout);
            Output.Close();
            Input.Close();
        }
    }

    /// <summary>
    /// A blocking, read-side queue of chunks. <see cref="WaitUntilConsumed"/> returns once every
    /// pushed chunk has been handed to a reader and that reader has come back for more — or, when
    /// the reader is holding the chunk, once it has had ample time to drop it.
    /// </summary>
    private sealed class QueueStream : Stream
    {
        private readonly BlockingCollection<byte[]> _chunks = new();
        private readonly SemaphoreSlim _readEntered = new(0);
        private int _pushed;
        private int _taken;

        public void Push(byte[] chunk)
        {
            Interlocked.Increment(ref _pushed);
            _chunks.Add(chunk);
        }

        public void WaitUntilConsumed()
        {
            // The reader re-entering Read after taking the last chunk means that chunk has been
            // passed to the output callback and returned from it.
            SpinWait.SpinUntil(() => Volatile.Read(ref _taken) == Volatile.Read(ref _pushed), Timeout);
            _readEntered.Wait(TimeSpan.FromMilliseconds(300));
        }

        public bool WaitUntilDrained(TimeSpan timeout)
            => SpinWait.SpinUntil(() => Volatile.Read(ref _taken) == Volatile.Read(ref _pushed), timeout);

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Volatile.Read(ref _taken) > 0)
                _readEntered.Release();
            try
            {
                var chunk = _chunks.Take();
                Interlocked.Increment(ref _taken);
                var n = Math.Min(count, chunk.Length);
                Array.Copy(chunk, 0, buffer, offset, n);
                return n;
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }

        public override void Write(byte[] buffer, int offset, int count) { }
        public override void Flush() { }

        public override void Close()
        {
            _chunks.CompleteAdding();
            base.Close();
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
