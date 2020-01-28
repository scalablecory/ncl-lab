using System;
using System.Net.Sockets;
using System.Threading;

namespace NclLab.Sockets
{
    /// <summary>
    /// A multiplexer for registered sockets.
    /// </summary>
    public sealed class RegisteredMultiplexer : IDisposable
    {
        private readonly EventWaitHandle _waitHandle;
        private readonly RegisteredWaitHandle _registeredWaitHandle;
        private readonly Interop.SafeRioCompletionQueueHandle _completionQueue;
        private uint _currentQueueSize = 4;

        public RegisteredMultiplexer()
        {
            Interop.Rio.Init();

            _waitHandle = new AutoResetEvent(initialState: false);
            _completionQueue = Interop.Rio.CreateCompletionQueue(_currentQueueSize, _waitHandle.SafeWaitHandle);
            _registeredWaitHandle = ThreadPool.UnsafeRegisterWaitForSingleObject(_waitHandle,
                (state, timedOut) => ((RegisteredMultiplexer)state).OnNotify(), this, millisecondsTimeOutInterval: -1, executeOnlyOnce: false);
            Notify();
        }

        public void Dispose()
        {
            _registeredWaitHandle.Unregister(_waitHandle);
            _completionQueue.Dispose();
            _waitHandle.Dispose();
        }

        internal Interop.SafeRioRequestQueueHandle RegisterSocket(SafeSocketHandle socketHandle)
        {
            lock (_completionQueue)
            {
                while (true)
                {
                    try
                    {
                        return Interop.Rio.CreateRequestQueue(socketHandle, _completionQueue, IntPtr.Zero, 1, 1, 1, 1);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
                    {
                        uint newQueueSize = _currentQueueSize * 2;
                        Interop.Rio.ResizeCompletionQueue(_completionQueue, newQueueSize);
                        _currentQueueSize = newQueueSize;
                    }
                }
            }
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

                for (int i = 0; i < dequeued; ++i)
                {
                    ref Interop.Rio.RIORESULT result = ref results[i];
                    RegisteredOperationContext.Complete(result.Status, result.BytesTransferred, new IntPtr(result.RequestContext));
                }
            }
            while (dequeued != 0);

            Notify();
        }
    }
}
