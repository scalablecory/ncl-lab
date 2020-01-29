using NclLab.Sockets;
using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace RegisteredServerSample
{
    class Program
    {
        static void Main(string[] args)
        {
            using var bufferPool = new RegisteredMemoryPool();
            using var multiplexer = new RegisteredMultiplexer();

            using var listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            listenSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listenSocket.Listen(int.MaxValue);

            async Task RunEchoClientAsync()
            {
                using Socket socket = RegisteredSocket.CreateRegisterableSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                await socket.ConnectAsync(listenSocket.LocalEndPoint);

                using var registeredSocket = new RegisteredSocket(multiplexer, socket);

                async Task<byte[]> RunEchoClientSendAsync()
                {
                    using IMemoryOwner<byte> memoryOwner = bufferPool.Rent();
                    Memory<byte> memory = memoryOwner.Memory;
                    RegisteredOperationContext ctx = registeredSocket.CreateOperationContext();

                    Random rng = new Random();
                    using SHA1 sha1 = SHA1.Create();

                    int bytesLeft = 1024 * 1024 * 1024;

                    while (bytesLeft != 0)
                    {
                        Memory<byte> buffer = memory.Slice(0, Math.Min(memory.Length, bytesLeft));
                        rng.NextBytes(buffer.Span);


                        await ctx.SendAsync(buffer);
                    }
                }
            }

            async Task RunListenLoopAsync()
            {
                while (true)
                {
                    Socket acceptSocket = RegisteredSocket.CreateRegisterableSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                    try
                    {
                        await listenSocket.AcceptAsync(acceptSocket);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
                    {
                        // Shutting down.
                        break;
                    }

                    _ = ProcessEchoConnectionAsync(acceptSocket);
                }
            }

            async Task ProcessEchoConnectionAsync(Socket acceptSocket)
            {
                using Socket socket = acceptSocket;
                using var registeredSocket = new RegisteredSocket(multiplexer, socket);
                using IMemoryOwner<byte> bufferOwner = bufferPool.Rent();

                RegisteredOperationContext ctx = registeredSocket.CreateOperationContext();
                Memory<byte> buffer = bufferOwner.Memory;

                while (true)
                {
                    int bytesReceived = await ctx.ReceiveAsync(buffer);

                    if (bytesReceived == 0)
                    {
                        break;
                    }

                    Memory<byte> sendBuffer = buffer.Slice(0, bytesReceived);

                    while (sendBuffer.Length != 0)
                    {
                        int bytesSent = await ctx.SendAsync(sendBuffer);
                        sendBuffer = sendBuffer.Slice(bytesSent);
                    }
                }

                socket.Shutdown(SocketShutdown.Both);
            }
        }
    }
}
