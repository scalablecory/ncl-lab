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
    public unsafe sealed class RegisteredOperationContext : IValueTaskSource<int>
    {
        private readonly RegisteredSocket _socket;
        private Interop.Rio.RIO_BUF[] _buffers;
        private RegisteredMemoryManager _bufferManager, _endPointManager;
        private ManualResetValueTaskSourceCore<int> _valueTaskSource;
        private GCHandle _thisHandle, _buffersHandle;

        internal RegisteredOperationContext(RegisteredSocket socket)
        {
            _socket = socket;
            _valueTaskSource.RunContinuationsAsynchronously = true;
            _buffers = new Interop.Rio.RIO_BUF[socket.ProtocolType == ProtocolType.Udp ? 2 : 1];
        }

        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> memory)
        {
            Prepare(memory);
            SocketError result = _socket.StartSend(_buffersHandle.AddrOfPinnedObject(), GCHandle.ToIntPtr(_thisHandle));
            CheckError(result);
            return new ValueTask<int>(this, _valueTaskSource.Version);
        }

        public ValueTask<int> SendToAsync(ReadOnlyMemory<byte> memory, RegisteredEndPoint remoteEndPoint)
        {
            Prepare(memory, remoteEndPoint);
            SocketError result = _socket.StartSendTo(_buffersHandle.AddrOfPinnedObject(), GetEndPointAddress(), GCHandle.ToIntPtr(_thisHandle));
            CheckError(result);
            return new ValueTask<int>(this, _valueTaskSource.Version);
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> memory)
        {
            Prepare(memory);
            SocketError result = _socket.StartReceive(_buffersHandle.AddrOfPinnedObject(), GCHandle.ToIntPtr(_thisHandle));
            CheckError(result);
            return new ValueTask<int>(this, _valueTaskSource.Version);
        }

        public ValueTask<int> ReceiveFromAsync(Memory<byte> memory, RegisteredEndPoint remoteEndPoint)
        {
            Prepare(memory, remoteEndPoint);
            SocketError result = _socket.StartReceiveFrom(_buffersHandle.AddrOfPinnedObject(), GetEndPointAddress(), GCHandle.ToIntPtr(_thisHandle));
            CheckError(result);
            return new ValueTask<int>(this, _valueTaskSource.Version);
        }

        private IntPtr GetEndPointAddress()
        {
            return IntPtr.Add(_buffersHandle.AddrOfPinnedObject(), sizeof(Interop.Rio.RIO_BUF));
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
            _bufferManager = SetBuffer(ref _buffers[0], buffer);
            Pin();
        }

        private void Prepare(ReadOnlyMemory<byte> buffer, RegisteredEndPoint remoteEndPoint)
        {
            _bufferManager = SetBuffer(ref _buffers[0], buffer);
            _endPointManager = SetBuffer(ref _buffers[1], remoteEndPoint.Memory);
            Pin();
        }

        private RegisteredMemoryManager SetBuffer(ref Interop.Rio.RIO_BUF buf, ReadOnlyMemory<byte> buffer)
        {
            if (!MemoryMarshal.TryGetMemoryManager(buffer, out RegisteredMemoryManager manager, out int start, out int length))
            {
                throw new Exception($"Buffers given to {nameof(RegisteredSocket)} must be obtained via a {nameof(RegisteredMemoryPool)}.");
            }

            buf.BufferId = manager.RioBufferId;
            buf.Offset = start;
            buf.Length = length;

            return manager;
        }

        private void Pin()
        {
            Debug.Assert(_buffersHandle.IsAllocated == false);
            Debug.Assert(_thisHandle.IsAllocated == false);

            _valueTaskSource.Reset();
            _buffersHandle = GCHandle.Alloc(_buffers, GCHandleType.Pinned);
            _thisHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        }

        private void UnPin()
        {
            Debug.Assert(_buffersHandle.IsAllocated);
            Debug.Assert(_thisHandle.IsAllocated);

            _buffersHandle.Free();
            _thisHandle.Free();

            _bufferManager = null;
            _endPointManager = null;
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
            var @this = (RegisteredOperationContext)GCHandle.FromIntPtr(thisPtr).Target;

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
