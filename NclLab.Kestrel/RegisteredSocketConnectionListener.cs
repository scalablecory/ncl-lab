using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using NclLab.Sockets;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NclLab.Kestrel
{
    internal sealed class RegisteredSocketConnectionListener : IConnectionListener, IDisposable
    {
        readonly RegisteredMultiplexer _multiplexer = new RegisteredMultiplexer();
        readonly RegisteredMemoryPool _pool = new RegisteredMemoryPool();
        readonly SocketTransportOptions _options;
        readonly RegisteredSocketListener _listener;

        public EndPoint EndPoint => _listener.LocalEndPoint;

        public RegisteredSocketConnectionListener(SocketTransportOptions options, AddressFamily addressFamily)
        {
            _options = options;
            _listener = new RegisteredSocketListener(_multiplexer, addressFamily, SocketType.Stream, ProtocolType.Tcp);
        }

        public void Dispose()
        {
            _listener.Dispose();
            _multiplexer.Dispose();
            _pool.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }

        public async ValueTask<ConnectionContext> AcceptAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                try
                {
                    RegisteredSocket acceptSocket = await _listener.Accept();

                    if (_options.NoDelay == true)
                    {
                        acceptSocket.NoDelay = true;
                    }

                    return new RegisteredSocketConnection(_pool, acceptSocket, _options?.MaxReadBufferSize, _options?.MaxWriteBufferSize);
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
            }
        }

        public void Bind(IPEndPoint endPoint)
        {
            if (endPoint.Address == IPAddress.IPv6Any)
            {
                _listener.DualMode = true;
            }

            try
            {
                _listener.Bind(endPoint);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                throw new AddressInUseException(ex.Message, ex);
            }

            _listener.Listen(int.MaxValue);
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
        {
            _listener.Dispose();
            return default;
        }
    }
}
