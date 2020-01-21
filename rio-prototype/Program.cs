using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace rio_prototype
{
    class Program
    {
        static async Task Main(string[] args)
        {
            using Socket socket = RegisteredSocket.CreateRegisterableSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new DnsEndPoint("microsoft.com", 80));
            Console.WriteLine($"Connected: {socket.LocalEndPoint} -> {socket.RemoteEndPoint}");

            using var bufferPool = new RegisteredMemoryPool();
            using var multiplexer = new RegisteredMultiplexer(queueSize: 10);
            using var registeredSocket = new RegisteredSocket(multiplexer, socket);

            await Task.WhenAll(DoSend(), DoReceive()).ConfigureAwait(false);

            async Task DoSend()
            {
                Memory<byte> sendBuffer = bufferPool.Rent(64).Memory;
                int bytes = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: microsoft.com\r\n\r\n", sendBuffer.Span);
                sendBuffer = sendBuffer.Slice(bytes);

                while (sendBuffer.Length != 0)
                {
                    int bytesSent = await registeredSocket.SendAsync(sendBuffer).ConfigureAwait(false);

                    if (bytesSent == 0)
                    {
                        break;
                    }

                    sendBuffer = sendBuffer.Slice(bytesSent);
                }
            }

            async Task DoReceive()
            {
                Memory<byte> recvBuffer = bufferPool.Rent(4096).Memory;
                while (true)
                {
                    int bytesReceived = await registeredSocket.ReceiveAsync(recvBuffer).ConfigureAwait(false);

                    if (bytesReceived == 0)
                    {
                        break;
                    }

                    Console.WriteLine(Encoding.ASCII.GetString(recvBuffer.Span.Slice(bytesReceived)));
                }
            }
        }
    }
}
