using System;
using System.Threading;
using System.Threading.Tasks;

namespace NclLab.Http
{
    /// <summary>
    /// Can have many derived types...
    /// Http11SerializedConnection (one single connection, one request at a time), Http11PooledConnection (use a connection pool of Http11SerializedConnections), Http2Connection...
    /// As well as fancier things like a connection that auto-negotiates/migrates across differently-versioned or alt-svc sub-connections
    /// 
    /// More or less equivalent to current HttpConnectionPool; SocketsHttpHandler would be refactored to use this intead.
    /// </summary>
    public abstract class HttpConnection : IAsyncDisposable
    {
        public abstract PreparedHeader PrepareHeader(ReadOnlySpan<byte> name);
        public abstract PreparedHeader PrepareHeader(ReadOnlySpan<char> name);
        public abstract PreparedHeader PrepareHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value);
        public abstract PreparedHeader PrepareHeader(ReadOnlySpan<char> name, ReadOnlySpan<char> value);

        /// <summary>
        /// Opens a pooled HTTP/1.1 connection, a new stream in HTTP/2, etc.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public abstract ValueTask<HttpRequestStream> CreateNewRequestStreamAsync(CancellationToken cancellationToken);

        public abstract ValueTask DisposeAsync();
    }
}
