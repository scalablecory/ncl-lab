﻿using NclLab.Sockets;
using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RegisteredSocketsSample
{
    class Program
    {
        static async Task Main(string[] args)
        {
            using var bufferPool = new RegisteredMemoryPool();
            using var multiplexer = new RegisteredMultiplexer();
            int clientIds = 0;

            await Task.WhenAll(RunOneClient(), RunOneClient(), RunOneClient(), RunOneClient(), RunOneClient()).ConfigureAwait(false);

            async Task RunOneClient()
            {
                int clientId = Interlocked.Increment(ref clientIds);

                using var socket = new RegisteredSocket(multiplexer, AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                await socket.ConnectAsync(new DnsEndPoint("microsoft.com", 80));
                Console.WriteLine($"{clientId}: connected ({socket.LocalEndPoint} -> {socket.RemoteEndPoint}).");

                await Task.WhenAll(DoSend(), DoReceive()).ConfigureAwait(false);
                Console.WriteLine($"{clientId}: done.");

                async Task DoSend()
                {
                    Console.WriteLine($"{clientId}: sending...");

                    var operationContext = socket.CreateOperationContext();
                    using IMemoryOwner<byte> sendBufferOwner = bufferPool.Rent(128);
                    Memory<byte> sendBuffer = sendBufferOwner.Memory;

                    int bytes = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: microsoft.com\r\nConnection: close\r\n\r\n", sendBuffer.Span);
                    sendBuffer = sendBuffer.Slice(0, bytes);

                    while (sendBuffer.Length != 0)
                    {
                        int bytesSent = await operationContext.SendAsync(sendBuffer).ConfigureAwait(false);

                        Console.WriteLine($"{clientId}: sent {bytesSent:N0} bytes.");

                        if (bytesSent == 0)
                        {
                            break;
                        }

                        sendBuffer = sendBuffer.Slice(bytesSent);
                    }

                    Console.WriteLine($"{clientId}: done sending.");
                    socket.Shutdown(SocketShutdown.Send);
                }

                async Task DoReceive()
                {
                    Console.WriteLine($"{clientId}: receiving...");

                    var operationContext = socket.CreateOperationContext();
                    using IMemoryOwner<byte> recvBufferOwner = bufferPool.Rent(4096);
                    Memory<byte> recvBuffer = recvBufferOwner.Memory;

                    while (true)
                    {
                        int bytesReceived = await operationContext.ReceiveAsync(recvBuffer).ConfigureAwait(false);

                        Console.WriteLine($"{clientId}: received {bytesReceived:N0} bytes.");

                        if (bytesReceived == 0)
                        {
                            break;
                        }
                    }

                    Console.WriteLine($"{clientId}: done receiving.");
                }
            }
        }
    }
}
