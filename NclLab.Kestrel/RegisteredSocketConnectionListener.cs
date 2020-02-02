using Microsoft.AspNetCore.Connections;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Net;
using System.Threading;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using NclLab.Sockets;
using System.Net.Sockets;
using System.Buffers;
using System.Diagnostics;
using System.Threading.Tasks.Sources;

namespace NclLab.Kestrel
{
    internal sealed class RegisteredSocketConnectionListener : SocketAsyncEventArgs, IConnectionListener, IValueTaskSource
    {
        readonly RegisteredMultiplexer _multiplexer = new RegisteredMultiplexer();
        readonly RegisteredMemoryPool _pool = new RegisteredMemoryPool();
        readonly SocketTransportOptions _options;
        readonly Socket _listenSocket;
        ManualResetValueTaskSourceCore<bool> _valueTaskSource;

        public EndPoint EndPoint => _listenSocket.LocalEndPoint;

        public RegisteredSocketConnectionListener(SocketTransportOptions options, Socket socket)
        {
            _options = options;
            _listenSocket = socket;
        }

        public ValueTask DisposeAsync()
        {
            _listenSocket.Dispose();
            _multiplexer.Dispose();
            _pool.Dispose();
            base.Dispose();
            return default;
        }

        public async ValueTask<ConnectionContext> AcceptAsync(CancellationToken cancellationToken = default)
        {
            AddressFamily af = EndPoint.AddressFamily;

            while (true)
            {
                _valueTaskSource.Reset();

                try
                {
                    AcceptSocket = RegisteredSocket.CreateRegisterableSocket(af, SocketType.Stream, ProtocolType.Tcp);

                    if (_options?.NoDelay == true)
                    {
                        AcceptSocket.NoDelay = true;
                    }

                    if (_listenSocket.AcceptAsync(this))
                    {
                        await new ValueTask(this, _valueTaskSource.Version);
                    }

                    var con = new RegisteredSocketConnection(_multiplexer, _pool, AcceptSocket, _options?.MaxReadBufferSize, _options?.MaxWriteBufferSize);
                    AcceptSocket = null;

                    return con;
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
                {
                    return null;
                }
                catch (SocketException)
                {
                    continue;
                }
                finally
                {
                    if (AcceptSocket != null)
                    {
                        AcceptSocket.Dispose();
                        AcceptSocket = null;
                    }
                }
            }
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
        {
            _listenSocket.Dispose();
            return default;
        }

        protected override void OnCompleted(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                _valueTaskSource.SetResult(false);
            }
            else
            {
                _valueTaskSource.SetException(new SocketException((int)e.SocketError));
            }
        }

        void IValueTaskSource.GetResult(short token)
        {
            _valueTaskSource.GetResult(token);
        }

        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
        {
            return _valueTaskSource.GetStatus(token);
        }

        void IValueTaskSource.OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _valueTaskSource.OnCompleted(continuation, state, token, flags);
        }
    }
}
