using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Options;
using NclLab.Sockets;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NclLab.Kestrel
{
    public sealed class RegisteredSocketTransportFactory : IConnectionListenerFactory
    {
        readonly SocketTransportOptions _options;

        public RegisteredSocketTransportFactory(IOptions<SocketTransportOptions> options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            _options = options.Value;
        }

        public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            if (!(endpoint is IPEndPoint ipEndPoint))
            {
                throw new NotSupportedException("EndPoint type not supported.");
            }

            Socket socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            if (ipEndPoint.Address == IPAddress.IPv6Any)
            {
                socket.DualMode = true;
            }

            if (_options.NoDelay)
            {
                socket.NoDelay = true;
            }

            try
            {
                socket.Bind(endpoint);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                throw new AddressInUseException(ex.Message, ex);
            }

            socket.Listen(int.MaxValue);

            return new ValueTask<IConnectionListener>(new RegisteredSocketConnectionListener(_options, socket));
        }
    }
}
