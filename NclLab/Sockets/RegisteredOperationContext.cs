using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace NclLab.Sockets
{
    /// <summary>
    /// Context for an async operation. Should be reused across multiple I/Os. Not concurrently.
    /// </summary>
    public unsafe sealed class RegisteredOperationContext : IValueTaskSource<int>, IDisposable
    {
        private readonly RegisteredSocket _socket;
        private Interop.Rio.RIO_BUF[] _buffers = new Interop.Rio.RIO_BUF[1];
        private RegisteredMemoryManager[] _rioBuffers = new RegisteredMemoryManager[1];
        private ManualResetValueTaskSourceCore<int> _valueTaskSource;
        private GCHandle _buffersHandle;

        // Used only to pin this context, as GCHandle can't pin a non-blittable type; RIO does not actually use overlapped.
        private readonly StrongBox<RegisteredOperationContext> _thisRef = new StrongBox<RegisteredOperationContext>();
        private readonly PreAllocatedOverlapped _preallocatedOverlapped;
        private NativeOverlapped* _overlapped;

        internal RegisteredOperationContext(RegisteredSocket socket)
        {
            _socket = socket;
            _preallocatedOverlapped = new PreAllocatedOverlapped(delegate { }, _thisRef, null);
            _valueTaskSource.RunContinuationsAsynchronously = true;
        }

        public void Dispose()
        {
            _preallocatedOverlapped.Dispose();
        }

        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> memory)
        {
            Prepare(memory);
            SocketError result = _socket.StartSend(_buffersHandle.AddrOfPinnedObject(), bufferCount: 1, new IntPtr(_overlapped));
            CheckError(result);
            return new ValueTask<int>(this, _valueTaskSource.Version);
        }

        public ValueTask<int> SendAsync(ReadOnlySpan<ReadOnlyMemory<byte>> memory)
        {
            Prepare(memory);
            SocketError result = _socket.StartSend(_buffersHandle.AddrOfPinnedObject(), memory.Length, new IntPtr(_overlapped));
            CheckError(result);
            return new ValueTask<int>(this, _valueTaskSource.Version);
        }

        public ValueTask<int> SendToAsync(ReadOnlyMemory<byte> memory, RegisteredEndPoint remoteEndPoint)
        {
            Prepare(memory);
            SocketError result = _socket.StartSendTo(_buffersHandle.AddrOfPinnedObject(), bufferCount: 1, GetEndPointAddress(bufferCount: 1), new IntPtr(_overlapped));
            CheckError(result);
            return new ValueTask<int>(this, _valueTaskSource.Version);
        }

        public ValueTask<int> SendToAsync(ReadOnlySpan<ReadOnlyMemory<byte>> memory, RegisteredEndPoint remoteEndPoint)
        {
            Prepare(memory);
            SocketError result = _socket.StartSendTo(_buffersHandle.AddrOfPinnedObject(), memory.Length, GetEndPointAddress(memory.Length), new IntPtr(_overlapped));
            CheckError(result);
            return new ValueTask<int>(this, _valueTaskSource.Version);
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> memory)
        {
            Prepare(memory);
            SocketError result = _socket.StartReceive(_buffersHandle.AddrOfPinnedObject(), bufferCount: 1, new IntPtr(_overlapped));
            CheckError(result);
            return new ValueTask<int>(this, _valueTaskSource.Version);
        }

        public ValueTask<int> ReceiveAsync(ReadOnlySpan<Memory<byte>> memory)
        {
            Prepare(memory);
            SocketError result = _socket.StartReceive(_buffersHandle.AddrOfPinnedObject(), memory.Length, new IntPtr(_overlapped));
            CheckError(result);
            return new ValueTask<int>(this, _valueTaskSource.Version);
        }

        public ValueTask<int> ReceiveFromAsync(Memory<byte> memory, RegisteredEndPoint remoteEndPoint)
        {
            Prepare(memory);
            SocketError result = _socket.StartReceiveFrom(_buffersHandle.AddrOfPinnedObject(), bufferCount: 1, GetEndPointAddress(bufferCount: 1), new IntPtr(_overlapped));
            CheckError(result);
            return new ValueTask<int>(this, _valueTaskSource.Version);
        }

        public ValueTask<int> ReceiveFromAsync(ReadOnlySpan<Memory<byte>> memory, RegisteredEndPoint remoteEndPoint)
        {
            Prepare(memory);
            SocketError result = _socket.StartReceiveFrom(_buffersHandle.AddrOfPinnedObject(), memory.Length, GetEndPointAddress(memory.Length), new IntPtr(_overlapped));
            CheckError(result);
            return new ValueTask<int>(this, _valueTaskSource.Version);
        }

        private IntPtr GetEndPointAddress(int bufferCount)
        {
            return IntPtr.Add(_buffersHandle.AddrOfPinnedObject(), sizeof(Interop.Rio.RIO_BUF) * bufferCount);
        }

        private void CheckError(SocketError error)
        {
            if (error != SocketError.Success && error != SocketError.IOPending)
            {
                Complete(error);
            }
        }

        private void Prepare(ReadOnlyMemory<byte> buffer)
        {
            SetBuffer(0, buffer);
            Pin();
        }

        private void Prepare(ReadOnlyMemory<byte> buffer, RegisteredEndPoint remoteEndPoint)
        {
            EnsureBuffersCount(2);
            SetBuffer(0, buffer);
            SetBuffer(1, remoteEndPoint.Memory);
            Pin();
        }

        private void Prepare(ReadOnlySpan<Memory<byte>> buffers)
        {
            EnsureBuffersCount(buffers.Length);
            for (int i = 0; i < buffers.Length; ++i)
            {
                SetBuffer(i, buffers[i]);
            }
            Pin();
        }

        private void Prepare(ReadOnlySpan<Memory<byte>> buffers, RegisteredEndPoint remoteEndPoint)
        {
            EnsureBuffersCount(buffers.Length + 1);
            for (int i = 0; i < buffers.Length; ++i)
            {
                SetBuffer(i, buffers[i]);
            }
            SetBuffer(buffers.Length, remoteEndPoint.Memory);
            Pin();
        }

        private void Prepare(ReadOnlySpan<ReadOnlyMemory<byte>> buffers)
        {
            EnsureBuffersCount(buffers.Length);
            for (int i = 0; i < buffers.Length; ++i)
            {
                SetBuffer(i, buffers[i]);
            }
            Pin();
        }

        private void Prepare(ReadOnlySpan<ReadOnlyMemory<byte>> buffers, RegisteredEndPoint remoteEndPoint)
        {
            EnsureBuffersCount(buffers.Length + 1);
            for (int i = 0; i < buffers.Length; ++i)
            {
                SetBuffer(i, buffers[i]);
            }
            SetBuffer(buffers.Length, remoteEndPoint.Memory);
            Pin();
        }

        private void EnsureBuffersCount(int bufferCount)
        {
            if (_buffers.Length < bufferCount)
            {
                _rioBuffers = new RegisteredMemoryManager[bufferCount];
                _buffers = new Interop.Rio.RIO_BUF[bufferCount];
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

        private void Pin()
        {
            _buffersHandle = GCHandle.Alloc(_buffers, GCHandleType.Pinned);
            _valueTaskSource.Reset();

            _thisRef.Value = this;
            _overlapped = _socket._boundHandle.AllocateNativeOverlapped(_preallocatedOverlapped);
        }

        private void UnPin()
        {
            Debug.Assert(_overlapped != null);

            _buffersHandle.Free();

            _socket._boundHandle.FreeNativeOverlapped(_overlapped);
            _overlapped = null;
            _thisRef.Value = null;

            for (int i = 0; i < _rioBuffers.Length; ++i)
            {
                _rioBuffers[i] = null;
            }
        }

        private void Complete(int transferred)
        {
            UnPin();
            _valueTaskSource.SetResult(transferred);
        }

        private void Complete(SocketError error)
        {
            UnPin();
            _valueTaskSource.SetException(new SocketException((int)error));
        }

        internal static unsafe void Complete(int errorCode, int transferred, IntPtr thisPtr)
        {
            var nativeOverlapped = (NativeOverlapped*)thisPtr.ToPointer();
            var box = (StrongBox<RegisteredOperationContext>)ThreadPoolBoundHandle.GetNativeOverlappedState(nativeOverlapped);
            RegisteredOperationContext @this = box.Value;

            if (errorCode == (int)SocketError.Success)
            {
                @this.Complete(transferred);
            }
            else
            {
                @this.Complete((SocketError)errorCode);
            }
        }

        int IValueTaskSource<int>.GetResult(short token)
            => _valueTaskSource.GetResult(token);

        ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token)
            => _valueTaskSource.GetStatus(token);

        void IValueTaskSource<int>.OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _valueTaskSource.OnCompleted(continuation, state, token, flags);
    }
}
