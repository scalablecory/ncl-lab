using System;
using System.Threading;

namespace rio_prototype
{
    public sealed class RegisteredMultiplexer : IDisposable
    {
        private readonly AutoResetEvent _waitHandle;
        private readonly RegisteredWaitHandle _registeredWaitHandle;
        private readonly Interop.SafeRioCompletionQueueHandle _completionQueue;

        internal Interop.SafeRioCompletionQueueHandle SafeHandle => _completionQueue;

        public RegisteredMultiplexer(uint queueSize)
        {
            Interop.Rio.Init();

            _waitHandle = new AutoResetEvent(false);
            _completionQueue = Interop.Rio.CreateCompletionQueue(queueSize, _waitHandle.SafeWaitHandle);
            _registeredWaitHandle = ThreadPool.UnsafeRegisterWaitForSingleObject(_waitHandle,
                (state, timedOut) => ((RegisteredMultiplexer)state).OnNotify(), this, -1, false);

            Notify();
        }

        public void Dispose()
        {
            _registeredWaitHandle.Unregister(_waitHandle);
            _completionQueue.Dispose();
            _waitHandle.Dispose();
        }

        private void Notify()
        {
            lock (_completionQueue)
            {
                Interop.Rio.Notify(_completionQueue);
            }
        }

        private void OnNotify()
        {
            Span<Interop.Rio.RIORESULT> results = stackalloc Interop.Rio.RIORESULT[32];
            int dequeued;

            do
            {
                lock (_completionQueue)
                {
                    dequeued = Interop.Rio.DequeueCompletions(_completionQueue, results);
                }

                Console.WriteLine($"Dequeued {dequeued} notifications.");

                for (int i = 0; i < dequeued; ++i)
                {
                    ref Interop.Rio.RIORESULT result = ref results[i];
                    RegisteredOperationEventArgs.Complete(result.Status, result.BytesTransferred, new IntPtr(result.RequestContext));
                }
            }
            while (dequeued != 0);

            Notify();
        }
    }
}
