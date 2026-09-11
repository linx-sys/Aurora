using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Aurora.Tests
{
    public class ImportRegressionTests
    {
        static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        [Fact]
        public async Task Queue_WaitsForUiCommitBeforeNextImport()
        {
            using var queue = new LibraryWorkQueue();
            var entered = Signal();
            var uiCommit = Signal();
            var events = new List<string>();
            Task scan = queue.Enqueue(async token =>
            {
                events.Add("scan");
                entered.SetResult(true);
                await uiCommit.Task;
                events.Add("replace");
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task drop = queue.Enqueue(token => { events.Add("append"); return Task.CompletedTask; });
            Assert.False(drop.IsCompleted);
            uiCommit.SetResult(true);
            await Task.WhenAll(scan, drop).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "scan", "replace", "append" }, events);
        }

        [Fact]
        public async Task Queue_FailureDoesNotPoisonLaterWork()
        {
            using var queue = new LibraryWorkQueue();
            Task failed = queue.Enqueue(_ => throw new InvalidOperationException("测试失败"));
            bool recovered = false;
            Task next = queue.Enqueue(_ => { recovered = true; return Task.CompletedTask; });
            await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
            await next.WaitAsync(TimeSpan.FromSeconds(5));
            await queue.Completion;
            Assert.True(recovered);
        }

        [Fact]
        public async Task Queue_CancelledRequestDoesNotDropFollowingRequest()
        {
            using var queue = new LibraryWorkQueue();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            bool executed = false, recovered = false;
            Task cancelled = queue.Enqueue(_ => { executed = true; return Task.CompletedTask; }, cancellation.Token);
            Task following = queue.Enqueue(_ => { recovered = true; return Task.CompletedTask; });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            await following.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(executed);
            Assert.True(recovered);
        }

        [Fact]
        public async Task Queue_DisposeCancelsActiveAndQueuedWorkAndDrains()
        {
            var queue = new LibraryWorkQueue();
            var entered = Signal();
            bool pendingRan = false;
            Task active = queue.Enqueue(async token =>
            {
                entered.SetResult(true);
                await Task.Delay(Timeout.Infinite, token);
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task pending = queue.Enqueue(_ => { pendingRan = true; return Task.CompletedTask; });
            queue.Dispose();
            queue.Dispose();
            await queue.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.Enqueue(_ => Task.CompletedTask));
            Assert.False(pendingRan);
        }
    }
}
