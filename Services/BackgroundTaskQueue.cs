using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace LiuYun.Services
{

    public class BackgroundTaskQueue : IDisposable
    {
        private readonly Channel<Func<CancellationToken, Task>> _queue;
        private readonly CancellationTokenSource _disposalTokenSource;
        private readonly Task[] _processingTasks;
        private readonly int _workerCount;

        public BackgroundTaskQueue(int capacity = 5000, int workerCount = 1)
        {
            _workerCount = workerCount;

            var options = new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait
            };

            _queue = Channel.CreateBounded<Func<CancellationToken, Task>>(options);
            _disposalTokenSource = new CancellationTokenSource();

            _processingTasks = new Task[_workerCount];
            for (int i = 0; i < _workerCount; i++)
            {
                int workerId = i;
                _processingTasks[i] = Task.Run(() => ProcessQueueAsync(_disposalTokenSource.Token, workerId));
            }
        }

        public async ValueTask QueueBackgroundWorkItemAsync(Func<CancellationToken, Task> workItem)
        {
            if (workItem == null)
            {
                throw new ArgumentNullException(nameof(workItem));
            }

            await _queue.Writer.WriteAsync(workItem);
        }

        private async Task ProcessQueueAsync(CancellationToken cancellationToken, int workerId)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"BackgroundTaskQueue worker {workerId} started");

                await foreach (var workItem in _queue.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        await workItem(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Background task error (worker {workerId}): {ex.Message}");
                    }
                }

                System.Diagnostics.Debug.WriteLine($"BackgroundTaskQueue worker {workerId} stopped");
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"BackgroundTaskQueue worker {workerId} canceled");
            }
        }

        public void Dispose()
        {
            _disposalTokenSource.Cancel();
            _queue.Writer.Complete();

            Task completion = Task.WhenAll(_processingTasks);
            _ = completion.ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                    {
                        System.Diagnostics.Debug.WriteLine($"BackgroundTaskQueue shutdown ended with error: {task.Exception?.GetBaseException().Message}");
                    }

                    _disposalTokenSource.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}

