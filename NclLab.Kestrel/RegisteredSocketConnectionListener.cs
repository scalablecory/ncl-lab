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

namespace NclLab.Kestrel
{
    internal sealed class RegisteredSocketConnectionListener : IConnectionListener
    {
        readonly RegisteredMultiplexer _multiplexer = new RegisteredMultiplexer();
        readonly RegisteredMemoryPool _pool = new RegisteredMemoryPool();
        readonly SocketTransportOptions _options;
        readonly Socket _socket;

        public EndPoint EndPoint => _socket.LocalEndPoint;

        public RegisteredSocketConnectionListener(SocketTransportOptions options, Socket socket)
        {
            _options = options;
            _socket = socket;
        }

        public ValueTask DisposeAsync()
        {
            _socket.Dispose();
            _multiplexer.Dispose();
            _pool.Dispose();
            return default;
        }

        public async ValueTask<ConnectionContext> AcceptAsync(CancellationToken cancellationToken = default)
        {
            AddressFamily af = EndPoint.AddressFamily;

            while (true)
            {
                Socket acceptSocket = null;

                try
                {
                    acceptSocket = RegisteredSocket.CreateRegisterableSocket(af, SocketType.Stream, ProtocolType.Tcp);

                    await _socket.AcceptAsync(acceptSocket);

                    return new RegisteredSocketConnection(_pool, _multiplexer, _socket);
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
                    acceptSocket?.Dispose();
                }
            }
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
        {
            _socket.Dispose();
            return default;
        }
    }
}
