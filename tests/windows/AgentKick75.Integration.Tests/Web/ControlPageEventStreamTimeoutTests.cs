// SPDX-License-Identifier: MIT
using AgentKick75.App.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace AgentKick75.Integration.Tests.Web;

public sealed class ControlPageEventStreamTimeoutTests
{
    [Theory]
    [InlineData(BlockedResponseOperation.Write)]
    [InlineData(BlockedResponseOperation.Flush)]
    public async Task StreamEventsCoreAsync_SlowResponse_AbortsAndReleasesResources(
        BlockedResponseOperation blockedOperation)
    {
        using var lifetime = new RecordingRequestLifetimeFeature();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        await using var responseBody = new BlockingResponseStream(blockedOperation);
        context.Response.Body = responseBody;

        var controlPlane = new BlockingEventControlPlane();
        using var eventStreamSlots = new SemaphoreSlim(1, 1);

        Task stream = ControlPageEndpoints.StreamEventsCoreAsync(
            context,
            controlPlane,
            eventStreamSlots,
            TimeSpan.FromMilliseconds(25));

        await controlPlane.SubscriptionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(
            () => stream.WaitAsync(TimeSpan.FromSeconds(2)));
        await controlPlane.SubscriptionReleased.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains("did not accept", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, lifetime.AbortCount);
        Assert.True(lifetime.RequestAborted.IsCancellationRequested);
        Assert.Equal(0, controlPlane.ActiveSubscriptionCount);
        Assert.Equal(1, eventStreamSlots.CurrentCount);
    }

    public enum BlockedResponseOperation
    {
        Write,
        Flush,
    }

    private sealed class RecordingRequestLifetimeFeature : IHttpRequestLifetimeFeature, IDisposable
    {
        private readonly CancellationTokenSource requestAbortedSource = new();
        private int abortCount;

        public RecordingRequestLifetimeFeature()
        {
            RequestAborted = requestAbortedSource.Token;
        }

        public CancellationToken RequestAborted { get; set; }

        public int AbortCount => Volatile.Read(ref abortCount);

        public void Abort()
        {
            Interlocked.Increment(ref abortCount);
            requestAbortedSource.Cancel();
        }

        public void Dispose()
        {
            requestAbortedSource.Dispose();
        }
    }

    private sealed class BlockingResponseStream(BlockedResponseOperation blockedOperation) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            throw new NotSupportedException("Only asynchronous response I/O is expected.");
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return blockedOperation == BlockedResponseOperation.Flush
                ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                : Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("Only asynchronous response I/O is expected.");
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            return blockedOperation == BlockedResponseOperation.Write
                ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                : Task.CompletedTask;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            return blockedOperation == BlockedResponseOperation.Write
                ? new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingEventControlPlane : IControlPlane
    {
        private int activeSubscriptionCount;

        public TaskCompletionSource SubscriptionStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SubscriptionReleased { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ActiveSubscriptionCount => Volatile.Read(ref activeSubscriptionCount);

        public ValueTask<ControlStatusDto> GetStatusAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask<ControlSettingsDto> GetSettingsAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask<ControlSettingsDto> ApplySettingsAsync(
            ControlSettingsDto settings,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask PreviewAsync(
            ControlPreviewState state,
            TimeSpan duration,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask<ControlStatusDto> SetPausedAsync(
            bool isPaused,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask RestoreOriginalLightingAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask<HardwareTestResultDto> RunHardwareTestAsync(
            HardwareTestRequestDto request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<ControlEventDto> WatchEventsAsync(
            CancellationToken cancellationToken)
        {
            return new BlockingEventEnumerable(this, cancellationToken);
        }

        private sealed class BlockingEventEnumerable(
            BlockingEventControlPlane owner,
            CancellationToken cancellationToken) : IAsyncEnumerable<ControlEventDto>
        {
            public IAsyncEnumerator<ControlEventDto> GetAsyncEnumerator(
                CancellationToken enumerationCancellationToken = default)
            {
                CancellationToken effectiveToken = enumerationCancellationToken.CanBeCanceled
                    ? enumerationCancellationToken
                    : cancellationToken;
                return new BlockingEventEnumerator(owner, effectiveToken);
            }
        }

        private sealed class BlockingEventEnumerator : IAsyncEnumerator<ControlEventDto>
        {
            private readonly BlockingEventControlPlane owner;
            private readonly TaskCompletionSource<bool> completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly CancellationTokenRegistration cancellationRegistration;
            private int disposed;

            public BlockingEventEnumerator(
                BlockingEventControlPlane owner,
                CancellationToken cancellationToken)
            {
                this.owner = owner;
                Interlocked.Increment(ref owner.activeSubscriptionCount);
                owner.SubscriptionStarted.TrySetResult();
                cancellationRegistration = cancellationToken.Register(
                    () => completion.TrySetResult(false));
            }

            public ControlEventDto Current => throw new InvalidOperationException();

            public ValueTask<bool> MoveNextAsync()
            {
                return new ValueTask<bool>(completion.Task);
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    cancellationRegistration.Dispose();
                    completion.TrySetResult(false);
                    Interlocked.Decrement(ref owner.activeSubscriptionCount);
                    owner.SubscriptionReleased.TrySetResult();
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
