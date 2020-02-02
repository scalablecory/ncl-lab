using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace NclLab.Sockets
{
    public sealed class RegisteredSocketListener : IDisposable
    {
        readonly ValueTaskEventArgs _args;
        readonly Socket _socket;
        volatile bool _disposed;

        public EndPoint LocalEndPoint => _socket.LocalEndPoint;
        public EndPoint RemoteEndPoint => _socket.RemoteEndPoint;

        public bool DualMode
        {
            get => _socket.DualMode;
            set => _socket.DualMode = value;
        }

        public RegisteredSocketListener(RegisteredMultiplexer multiplexer, AddressFamily family, SocketType socketType, ProtocolType protocolType)
        {
            _args = new ValueTaskEventArgs(multiplexer);
            _socket = RegisteredSocket.CreateRegisterableSocket(family, socketType, protocolType);
        }

        public void Bind(IPEndPoint endPoint)
        {
            _socket.Bind(endPoint);
        }

        public void Listen(int backlog)
        {
            _socket.Listen(backlog);
        }

        public ValueTask<RegisteredSocket> Accept()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RegisteredSocketListener));

            _args.Reset();

            _args.AcceptSocket = RegisteredSocket.CreateRegisterableSocket(_socket.AddressFamily, _socket.SocketType, _socket.ProtocolType);

            if (!_socket.AcceptAsync(_args))
            {
                _args.Complete();
            }

            return _args.Task;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Disposing a socket does not cancel the AcceptEx call when WSA_FLAG_REGISTERED_IO is specified.
            if (!Interop.Kernel32.CancelIoEx(_socket.SafeHandle, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != Interop.Kernel32.ERROR_NOT_FOUND)
                {
                    throw new SocketException((int)err);
                }
            }

            _socket.Dispose();
            _args.Dispose();
        }

        private class ValueTaskEventArgs : SocketAsyncEventArgs, IValueTaskSource<RegisteredSocket>
        {
            readonly RegisteredMultiplexer _multiplexer;
            ManualResetValueTaskSourceCore<RegisteredSocket> _valueTaskSource;

            public ValueTaskEventArgs(RegisteredMultiplexer multiplexer)
            {
                _multiplexer = multiplexer;
            }

            public void Complete()
            {
                OnCompleted(this);
            }

            public ValueTask<RegisteredSocket> Task => new ValueTask<RegisteredSocket>(this, _valueTaskSource.Version);

            protected override void OnCompleted(SocketAsyncEventArgs e)
            {
                if (SocketError != SocketError.Success)
                {
                    AcceptSocket.Dispose();
                    AcceptSocket = null;
                    _valueTaskSource.SetException(new SocketException((int)SocketError));
                    return;
                }

                RegisteredSocket socket;

                try
                {
                    socket = new RegisteredSocket(_multiplexer, e.AcceptSocket);
                }
                catch (Exception ex)
                {
                    AcceptSocket.Dispose();
                    AcceptSocket = null;
                    _valueTaskSource.SetException(ex);
                    return;
                }

                _valueTaskSource.SetResult(socket);
            }

            public void Reset()
            {
                _valueTaskSource.Reset();
            }

            RegisteredSocket IValueTaskSource<RegisteredSocket>.GetResult(short token)
            {
                return _valueTaskSource.GetResult(token);
            }

            ValueTaskSourceStatus IValueTaskSource<RegisteredSocket>.GetStatus(short token)
            {
                return _valueTaskSource.GetStatus(token);
            }

            void IValueTaskSource<RegisteredSocket>.OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
            {
                _valueTaskSource.OnCompleted(continuation, state, token, flags);
            }
        }
    }
}
