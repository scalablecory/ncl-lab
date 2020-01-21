using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace rio_prototype
{
    internal unsafe sealed class RegisteredOperationEventArgs : IValueTaskSource<int>
    {
        private static Func<Socket, ThreadPoolBoundHandle> s_GetOrAllocateThreadPoolBoundHandle;
        private RegisteredSocket _socket;
        private Interop.Rio.RIO_BUF[] _buffers = new Interop.Rio.RIO_BUF[1];
        private RegisteredMemoryManager[] _rioBuffers = new RegisteredMemoryManager[1];
        private ManualResetValueTaskSourceCore<int> _valueTaskSource;
        private GCHandle _buffersHandle;
        private bool _isSend;

        // Used only to pin this eventargs; RIO does not use overlapped here.
        private ThreadPoolBoundHandle _boundHandle;
        private readonly StrongBox<RegisteredOperationEventArgs> _thisRef = new StrongBox<RegisteredOperationEventArgs>();
        private readonly PreAllocatedOverlapped _preallocatedOverlapped;
        private NativeOverlapped* _overlapped;

        public RegisteredOperationEventArgs()
        {
            _preallocatedOverlapped = new PreAllocatedOverlapped((e, t, o) => { }, _thisRef, null);
            _valueTaskSource.RunContinuationsAsynchronously = true;
        }

        public (ValueTask<int>, IntPtr thisPtr, IntPtr buffersPtr) Prepare(RegisteredSocket socket, bool isSend, ReadOnlyMemory<byte> buffer)
        {
            if (_overlapped != null) throw new InvalidOperationException();

            SetBuffers(buffer);
            return Prepare(socket, isSend);
        }

        public (ValueTask<int>, IntPtr thisPtr, IntPtr buffersPtr) Prepare(RegisteredSocket socket, bool isSend, ReadOnlySpan<ReadOnlyMemory<byte>> buffers)
        {
            if (_overlapped != null) throw new InvalidOperationException();

            SetBuffers(buffers);
            return Prepare(socket, isSend);
        }

        public (ValueTask<int>, IntPtr thisPtr, IntPtr buffersPtr) Prepare(RegisteredSocket socket, bool isSend, ReadOnlySpan<Memory<byte>> buffers)
        {
            if (_overlapped != null) throw new InvalidOperationException();

            SetBuffers(buffers);
            return Prepare(socket, isSend);
        }

        private (ValueTask<int>, IntPtr thisPtr, IntPtr buffersPtr) Prepare(RegisteredSocket socket, bool isSend)
        {
            _socket = socket;
            _isSend = isSend;
            _buffersHandle = GCHandle.Alloc(_buffers, GCHandleType.Pinned);
            _valueTaskSource.Reset();

            _boundHandle = GetOrAllocateThreadPoolBoundHandle(socket._socket);
            _thisRef.Value = this;
            _overlapped = _boundHandle.AllocateNativeOverlapped(_preallocatedOverlapped);

            ValueTask<int> task = new ValueTask<int>(this, _valueTaskSource.Version);
            return (task, new IntPtr(_overlapped), _buffersHandle.AddrOfPinnedObject());
        }

        private void SetBuffers(ReadOnlyMemory<byte> buffer)
        {
            SetBuffer(0, buffer);
        }

        private void SetBuffers(ReadOnlySpan<Memory<byte>> buffers)
        {
            EnsureBuffersCount(buffers.Length);
            for (int i = 0; i < buffers.Length; ++i)
            {
                SetBuffer(i, buffers[i]);
            }
        }

        private void SetBuffers(ReadOnlySpan<ReadOnlyMemory<byte>> buffers)
        {
            EnsureBuffersCount(buffers.Length);
            for (int i = 0; i < buffers.Length; ++i)
            {
                SetBuffer(i, buffers[i]);
            }
        }

        private void EnsureBuffersCount(int bufferCount)
        {
            if (_buffers.Length < bufferCount)
            {
                Array.Resize(ref _rioBuffers, bufferCount);
                Array.Resize(ref _buffers, bufferCount);
            }
        }

        private void SetBuffer(int idx, ReadOnlyMemory<byte> buffer)
        {
            if (!MemoryMarshal.TryGetMemoryManager(buffer, out RegisteredMemoryManager manager, out int start, out int length))
            {
                throw new Exception($"Buffers given to {nameof(RegisteredSocket)} must be obtained via a {nameof(RegisteredMemoryPool)}.");
            }

            _buffers[idx].BufferId = manager.RioBufferId;
            _buffers[idx].Offset = start;
            _buffers[idx].Length = length;
            _rioBuffers[idx] = manager;
        }

        public void Complete(Exception exception, int transferred)
        {
            Debug.Assert(_overlapped != null);

            RegisteredSocket socket = _socket;
            _socket = null;
            _buffersHandle.Free();

            _boundHandle.FreeNativeOverlapped(_overlapped);
            _boundHandle = null;
            _overlapped = null;
            _thisRef.Value = null;

            if (exception == null)
            {
                _valueTaskSource.SetResult(transferred);
            }
            else
            {
                _valueTaskSource.SetException(exception);
            }

            Interlocked.Exchange(ref _isSend ? ref socket._cachedSendArgs : ref socket._cachedRecvArgs, this);
        }

        public static unsafe void Complete(int errorCode, int transferred, IntPtr thisPtr)
        {
            var nativeOverlapped = (NativeOverlapped*)thisPtr.ToPointer();
            var box = (StrongBox<RegisteredOperationEventArgs>)ThreadPoolBoundHandle.GetNativeOverlappedState(nativeOverlapped);

            box.Value.Complete(errorCode == 0 ? null : new SocketException(errorCode), transferred);
        }

        private static ThreadPoolBoundHandle GetOrAllocateThreadPoolBoundHandle(Socket socket)
        {
            if (s_GetOrAllocateThreadPoolBoundHandle == null)
            {
                MethodInfo method = typeof(Socket).GetMethod("GetOrAllocateThreadPoolBoundHandle", BindingFlags.Instance | BindingFlags.NonPublic);
                s_GetOrAllocateThreadPoolBoundHandle = (Func<Socket, ThreadPoolBoundHandle>)Delegate.CreateDelegate(typeof(Func<Socket, ThreadPoolBoundHandle>), method);
            }

            return s_GetOrAllocateThreadPoolBoundHandle(socket);
        }

        int IValueTaskSource<int>.GetResult(short token)
            => _valueTaskSource.GetResult(token);

        ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token)
            => _valueTaskSource.GetStatus(token);

        void IValueTaskSource<int>.OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _valueTaskSource.OnCompleted(continuation, state, token, flags);
    }
}
