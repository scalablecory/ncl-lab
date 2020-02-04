using System;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace NclLab.Sockets
{
    /// <summary>
    /// A registered socket.
    /// </summary>
    public sealed class RegisteredSocket : IDisposable
    {
        private static Func<SafeSocketHandle, AddressFamily, SocketType, ProtocolType, Socket> s_createRegisterableSocket;
        private readonly RegisteredMultiplexer _multiplexer;
        private readonly Socket _socket;
        private readonly Interop.SafeRioRequestQueueHandle _requestQueue;
        private uint _currentSendQueueSize = 1, _currentReceiveQueueSize = 1;

        public EndPoint LocalEndPoint => _socket.LocalEndPoint;
        public EndPoint RemoteEndPoint => _socket.RemoteEndPoint;
        public AddressFamily AddressFamily => _socket.AddressFamily;
        public SocketType SocketType => _socket.SocketType;
        public ProtocolType ProtocolType => _socket.ProtocolType;

        public bool NoDelay
        {
            get => _socket.NoDelay;
            set => _socket.NoDelay = value;
        }

        public RegisteredSocket(RegisteredMultiplexer multiplexer, AddressFamily family, SocketType socketType, ProtocolType protocolType)
        {
            Socket socket = CreateRegisterableSocket(family, socketType, protocolType);

            try
            {
                _requestQueue = multiplexer.RegisterSocket(socket.SafeHandle);
                _socket = socket;
                _multiplexer = multiplexer;
            }
            catch
            {
                socket.Dispose();
            }
        }

        /// <summary>
        /// Registers a socket against a multiplexer.
        /// </summary>
        /// <param name="multiplexer">The multiplexer to register a socket with.</param>
        /// <param name="socket">The socket to register. Must have been created using <see cref="CreateRegisterableSocket(AddressFamily, SocketType, ProtocolType)"/>.</param>
        internal RegisteredSocket(RegisteredMultiplexer multiplexer, Socket socket)
        {
            _requestQueue = multiplexer.RegisterSocket(socket.SafeHandle);
            _socket = socket;
        }

        public void Dispose()
        {
            _requestQueue.Dispose();
            _socket.Dispose();
        }

        public Task ConnectAsync(EndPoint endPoint)
        {
            return _socket.ConnectAsync(endPoint);
        }

        public void Shutdown(SocketShutdown how)
        {
            _socket.Shutdown(how);
        }

        public RegisteredOperationContext CreateOperationContext()
        {
            return new RegisteredOperationContext(this);
        }

        internal SocketError StartSend(IntPtr buffersPtr, IntPtr requestContext)
        {
            lock (_requestQueue)
            {
                while (true)
                {
                    SocketError err = Interop.Rio.Send(_requestQueue, buffersPtr, flags: 0, requestContext);
                    
                    if (err != SocketError.NoBufferSpaceAvailable)
                    {
                        return err;
                    }

                    GrowSendQueueUnlocked();
                }
            }
        }

        internal SocketError StartSendTo(IntPtr buffersPtr, IntPtr remoteAddressPtr, IntPtr requestContext)
        {
            lock (_requestQueue)
            {
                while (true)
                {
                    SocketError err = Interop.Rio.SendTo(_requestQueue, buffersPtr, remoteAddressPtr, flags: 0, requestContext);
                    
                    if (err != SocketError.NoBufferSpaceAvailable)
                    {
                        return err;
                    }

                    GrowSendQueueUnlocked();
                }
            }
        }

        internal SocketError StartReceive(IntPtr buffersPtr, IntPtr requestContext)
        {
            lock (_requestQueue)
            {
                while (true)
                {
                    SocketError err = Interop.Rio.Receive(_requestQueue, buffersPtr, flags: 0, requestContext);

                    if (err != SocketError.NoBufferSpaceAvailable)
                    {
                        return err;
                    }

                    GrowReceiveQueueUnlocked();
                }
            }
        }

        internal SocketError StartReceiveFrom(IntPtr buffersPtr, IntPtr remoteAddressPtr, IntPtr requestContext)
        {
            lock (_requestQueue)
            {
                while (true)
                {
                    SocketError err = Interop.Rio.ReceiveFrom(_requestQueue, buffersPtr, remoteAddressPtr, IntPtr.Zero, IntPtr.Zero, flags: 0, requestContext);

                    if (err != SocketError.NoBufferSpaceAvailable)
                    {
                        return err;
                    }

                    GrowReceiveQueueUnlocked();
                }
            }
        }

        private void GrowSendQueueUnlocked()
        {
            Debug.Assert(Monitor.IsEntered(_requestQueue));
            ResizeRequestQueueUnlocked(_currentReceiveQueueSize, checked(_currentSendQueueSize * 2));
        }

        private void GrowReceiveQueueUnlocked()
        {
            Debug.Assert(Monitor.IsEntered(_requestQueue));
            ResizeRequestQueueUnlocked(checked(_currentReceiveQueueSize * 2), _currentSendQueueSize);
        }

        private void ResizeRequestQueueUnlocked(uint newReceiveQueueSize, uint newSendQueueSize)
        {
            Debug.Assert(Monitor.IsEntered(_requestQueue));

            while (true)
            {
                try
                {
                    Interop.Rio.ResizeRequestQueue(_requestQueue, newReceiveQueueSize, newSendQueueSize);
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
                {
                    _multiplexer.GrowCompletionQueue();
                }
            }

            _currentReceiveQueueSize = newReceiveQueueSize;
            _currentSendQueueSize = newSendQueueSize;
        }

        internal static Socket CreateRegisterableSocket(AddressFamily family, SocketType socketType, ProtocolType protocolType)
        {
            Interop.Rio.Init();

            SafeSocketHandle socketHandle = Interop.Winsock.CreateRegisterableSocket((int)family, (int)socketType, (int)protocolType);

            return s_createRegisterableSocket != null ? s_createRegisterableSocket(socketHandle, family, socketType, protocolType) : SlowPath(socketHandle, family, socketType, protocolType);

            static Socket SlowPath(SafeSocketHandle socketHandle, AddressFamily family, SocketType socketType, ProtocolType protocolType)
            {
                ConstructorInfo ctor = typeof(Socket).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(SafeSocketHandle) }, null);
                FieldInfo addressFamilyField = typeof(Socket).GetField("_addressFamily", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo socketTypeField = typeof(Socket).GetField("_socketType", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo protocolTypeField = typeof(Socket).GetField("_protocolType", BindingFlags.Instance | BindingFlags.NonPublic);

                ParameterExpression socketHandleParameter = Expression.Parameter(typeof(SafeSocketHandle), nameof(socketHandle));
                ParameterExpression addressFamilyParameter = Expression.Parameter(typeof(AddressFamily), nameof(family));
                ParameterExpression socketTypeParameter = Expression.Parameter(typeof(SocketType), nameof(socketType));
                ParameterExpression protocolTypeParameter = Expression.Parameter(typeof(ProtocolType), nameof(protocolType));
                ParameterExpression socketVariable = Expression.Variable(typeof(Socket));

                LambdaExpression lambda =
                    Expression.Lambda(
                        typeof(Func<SafeSocketHandle, AddressFamily, SocketType, ProtocolType, Socket>),
                        Expression.Block(
                            new[] { socketVariable },
                            new Expression[]
                            {
                                Expression.Assign(socketVariable, Expression.New(ctor, socketHandleParameter)),
                                Expression.Assign(Expression.Field(socketVariable, addressFamilyField), addressFamilyParameter),
                                Expression.Assign(Expression.Field(socketVariable, socketTypeField), socketTypeParameter),
                                Expression.Assign(Expression.Field(socketVariable, protocolTypeField), protocolTypeParameter),
                                socketVariable
                            }),
                        new[] { socketHandleParameter, addressFamilyParameter, socketTypeParameter, protocolTypeParameter });
                s_createRegisterableSocket = (Func<SafeSocketHandle, AddressFamily, SocketType, ProtocolType, Socket>)lambda.Compile();

                return s_createRegisterableSocket(socketHandle, family, socketType, protocolType);
            }
        }
    }
}
