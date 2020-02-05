using System;
using System.Threading;
using System.Threading.Tasks;

namespace NclLab.Http
{
    /// <summary>
    /// Can have many derived types, and be layered:
    /// - Http11Connection (one single connection, one request at a time, no pooling)
    /// - Http11PooledConnection (issue against a pool of Http11Connection),
    /// - Http2Connection
    /// - Http3Connection
    /// - As well as fancier things like a connection that auto-negotiates/migrates across differently-versioned or alt-svc sub-connections
    /// - ProxiedConnection, etc..
    /// 
    /// More or less equivalent to current HttpConnectionPool type; SocketsHttpHandler would be refactored to use this intead.
    /// </summary>
    public abstract class HttpConnection : IAsyncDisposable
    {
        public abstract PreparedHeader PrepareHeader(ReadOnlySpan<byte> name);
        public abstract PreparedHeader PrepareHeader(ReadOnlySpan<char> name);
        public abstract PreparedHeader PrepareHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value);
        public abstract PreparedHeader PrepareHeader(ReadOnlySpan<char> name, ReadOnlySpan<char> value);

        /// <summary>
        /// Opens a new stream... can be pooled HTTP/1.1 connection, a new stream in HTTP/2, etc.
        /// </summary>
        public abstract ValueTask<HttpRequestStream> CreateNewRequestStreamAsync(CancellationToken cancellationToken);

        public abstract ValueTask<HttpRequestStream> AcceptRequestStream(CancellationToken cancellationToken);

        public abstract ValueTask DisposeAsync();
    }
}
