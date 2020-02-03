using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Options;
using System;
using System.Net;
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

            var listener = new RegisteredSocketConnectionListener(_options, ipEndPoint.AddressFamily);

            try
            {
                listener.Bind(ipEndPoint);
            }
            catch
            {
                listener.Dispose();
                throw;
            }

            return new ValueTask<IConnectionListener>(listener);
        }
    }
}
