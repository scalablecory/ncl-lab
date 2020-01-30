using NclLab.Sockets;
using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace RegisteredServerSample
{
    class Program
    {
        static async Task Main(string[] args)
        {
            int clientIds = 0, serverIds = 0;

            using var bufferPool = new RegisteredMemoryPool();
            using var multiplexer = new RegisteredMultiplexer();

            using var listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            listenSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listenSocket.Listen(int.MaxValue);

            await Task.WhenAll(RunAllClientsAsync(), RunListenLoopAsync());

            async Task RunAllClientsAsync()
            {
                var clients = new Task[100];
                for (int i = 0; i < clients.Length; ++i)
                {
                    clients[i] = RunEchoClientAsync();
                }

                try
                {
                    await Task.WhenAll(clients);

                }
                finally
                {
                    listenSocket.Dispose();
                }
            }

            async Task RunEchoClientAsync()
            {
                int clientId = Interlocked.Increment(ref clientIds);

                using Socket socket = RegisteredSocket.CreateRegisterableSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                await socket.ConnectAsync(listenSocket.LocalEndPoint);

                using var registeredSocket = new RegisteredSocket(multiplexer, socket);

                try
                {
                    await Task.WhenAll(RunEchoClientSendAsync(), RunEchoClientReceiveAsync());
                    Console.WriteLine($"C {clientId} finished successfully.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"C {clientId} failed: {ex.Message}");
                }

                async Task RunEchoClientSendAsync()
                {
                    try
                    {
                        using IMemoryOwner<byte> memoryOwner = bufferPool.Rent(bufferPool.MaxBufferSize);
                        Memory<byte> memory = memoryOwner.Memory;
                        RegisteredOperationContext ctx = registeredSocket.CreateOperationContext();

                        Random rng = new Random(clientId);
                        int bytesLeft = 1024 * 1024 * 100;

                        while (bytesLeft != 0)
                        {
                            Memory<byte> buffer = memory.Slice(0, Math.Min(memory.Length, bytesLeft));
                            rng.NextBytes(buffer.Span);

                            while (buffer.Length != 0)
                            {
                                int bytesSent = await ctx.SendAsync(buffer);
                                Console.WriteLine($"C {clientId} sent {bytesSent:N0} bytes.");
                                buffer = buffer.Slice(bytesSent);
                            }
                        }

                        socket.Shutdown(SocketShutdown.Send);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }

                async Task RunEchoClientReceiveAsync()
                {
                    try
                    {
                        using IMemoryOwner<byte> memoryOwner = bufferPool.Rent();
                        Memory<byte> memory = memoryOwner.Memory;
                        RegisteredOperationContext ctx = registeredSocket.CreateOperationContext();

                        Random rng = new Random(clientId);
                        byte[] rngBuffer = new byte[memory.Length];

                        while (true)
                        {
                            int bytesReceived = await ctx.ReceiveAsync(memory);

                            if (bytesReceived == 0)
                            {
                                break;
                            }

                            Console.WriteLine($"C {clientId} received {bytesReceived:N0} bytes.");
                            rng.NextBytes(rngBuffer.AsSpan(0, bytesReceived));

                            if (!rngBuffer.AsSpan(0, bytesReceived).SequenceEqual(memory.Span.Slice(0, bytesReceived)))
                            {
                                throw new Exception($"C {clientId} failed comparison.");
                            }
                        }

                        socket.Shutdown(SocketShutdown.Receive);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
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
                int serverId = Interlocked.Increment(ref serverIds);

                Console.WriteLine($"S {serverId} started.");

                try
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

                        Console.WriteLine($"S {serverId} received {bytesReceived:N0} bytes.");

                        Memory<byte> sendBuffer = buffer.Slice(0, bytesReceived);

                        while (sendBuffer.Length != 0)
                        {
                            int bytesSent = await ctx.SendAsync(sendBuffer);
                            sendBuffer = sendBuffer.Slice(bytesSent);
                            Console.WriteLine($"S {serverId} sent {bytesSent:N0} bytes.");
                        }
                    }

                    socket.Shutdown(SocketShutdown.Both);
                }
                catch(Exception ex)
                {
                    Console.WriteLine($"S {serverId} failed: {ex.Message}");
                }
            }
        }
    }
}
