using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace NclLab.Http
{
    /// <summary>
    /// More or less HttpRequestMessage/HttpResponseMessage/HttpContent rolled into one.
    /// </summary>
    public abstract class HttpRequestStream : IAsyncDisposable
    {
        // When Read() methods complete these will be filled in. Values are invalidated (memory re-used etc.) on next Read().
        // May want to return structs instead to avoid excessive vtable calls.
        public ReadOnlyMemory<byte> Uri { get; protected set; }
        public Version Version { get; protected set; }
        public HttpStatusCode StatusCode { get; protected set; }
        public ReadOnlyMemory<byte> HeaderName { get; protected set; }
        public ReadOnlyMemory<byte> HeaderValue { get; protected set; }

        // These non-async calls just fill a buffer that gets flushed upon WriteContent() or CompleteRequest().
        public abstract void WriteRequest(HttpMethod method, ReadOnlySpan<byte> uri);
        public abstract void WriteRequest(HttpMethod method, ReadOnlySpan<char> uri);

        public abstract void WriteResponse(HttpStatusCode statusCode);

        public abstract void WriteHeader(PreparedHeader header);
        public abstract void WriteHeader(PreparedHeader header, ReadOnlySpan<byte> value);
        public abstract void WriteHeader(PreparedHeader header, ReadOnlySpan<char> value);
        public abstract void WriteHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value);
        public abstract void WriteHeader(ReadOnlySpan<char> name, ReadOnlySpan<char> value);

        public abstract ValueTask WriteContentAsync(ReadOnlyMemory<byte> buffer);

        // Call ReadResponse() until non-1** response code, loop until ReadNextHeader() returns false, then loop until ReadContent() returns 0.
        // Note: no CancellationToken. Expected to give ctor a single CancellationToken to use for lifetime of request that we can reuse to avoid registering on every call.
        public abstract ValueTask ReadRequestAsync();
        public abstract ValueTask ReadResponseAsync();
        public abstract ValueTask<bool> ReadNextHeaderAsync();
        public abstract ValueTask<int> ReadContentAsync(Memory<byte> buffer);

        // Flush writes, drain, etc.
        public abstract ValueTask CompleteRequestAsync();

        public abstract ValueTask DisposeAsync();
    }
}
