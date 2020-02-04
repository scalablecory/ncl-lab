using System;
using System.Diagnostics;
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
        private readonly Thread _monitorThread;
        private readonly Interop.SafeRioCompletionQueueHandle _completionQueue;
        private uint _currentQueueSize = 4;
        private volatile bool _disposing;

        public RegisteredMultiplexer()
        {
            Interop.Rio.Init();

            _waitHandle = new AutoResetEvent(initialState: false);
            _completionQueue = Interop.Rio.CreateCompletionQueue(_currentQueueSize, _waitHandle.SafeWaitHandle);
            _monitorThread = new Thread(o => ((RegisteredMultiplexer)o).OnNotify());
            _monitorThread.Start(this);
        }

        public void Dispose()
        {
            _disposing = true;
            _waitHandle.Set();
            _monitorThread.Join();
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
                        return Interop.Rio.CreateRequestQueue(socketHandle, _completionQueue, IntPtr.Zero, 1, 1);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
                    {
                        GrowCompletionQueueUnlocked();
                    }
                }
            }
        }

        internal void GrowCompletionQueue()
        {
            lock (_completionQueue)
            {
                GrowCompletionQueueUnlocked();
            }
        }

        private void GrowCompletionQueueUnlocked()
        {
            Debug.Assert(Monitor.IsEntered(_completionQueue));
            uint newQueueSize = checked(_currentQueueSize * 2);
            Interop.Rio.ResizeCompletionQueue(_completionQueue, newQueueSize);
            _currentQueueSize = newQueueSize;
        }

        private void OnNotify()
        {
            Span<Interop.Rio.RIORESULT> results = stackalloc Interop.Rio.RIORESULT[32];

            do
            {
                lock (_completionQueue)
                {
                    Interop.Rio.Notify(_completionQueue);
                }

                _waitHandle.WaitOne();

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
            } while (!_disposing);
        }
    }
}
