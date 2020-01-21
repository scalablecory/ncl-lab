using System;
using System.Linq.Expressions;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace rio_prototype
{
    sealed class RegisteredSocket : IDisposable
    {
        private static Func<SafeSocketHandle, AddressFamily, SocketType, ProtocolType, Socket> s_createRegisterableSocket;
        internal readonly Socket _socket;
        private readonly Interop.SafeRioRequestQueueHandle _requestQueue;
        internal RegisteredOperationEventArgs _cachedSendArgs, _cachedRecvArgs;

        public RegisteredSocket(RegisteredMultiplexer multiplexer, Socket socket)
        {
            lock (multiplexer.SafeHandle)
            {
                _requestQueue = Interop.Rio.CreateRequestQueue(socket.SafeHandle, multiplexer.SafeHandle, IntPtr.Zero, 1, 1, 1, 1);
            }

            _socket = socket;
        }

        public void Dispose()
        {
            _requestQueue.Dispose();
        }

        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> memory)
        {
            RegisteredOperationEventArgs args = Interlocked.Exchange(ref _cachedSendArgs, null) ?? new RegisteredOperationEventArgs();

            (ValueTask<int> task, IntPtr requestContext, IntPtr buffersPtr) = args.Prepare(this, isSend: true, memory);
            try
            {
                lock (_requestQueue)
                {
                    Interop.Rio.Send(_requestQueue, buffersPtr, 1, 0, requestContext);
                }
            }
            catch (Exception ex)
            {
                args.Complete(ex, 0);
            }
            return task;
        }

        public ValueTask<int> SendAsync(ReadOnlySpan<ReadOnlyMemory<byte>> memory)
        {
            RegisteredOperationEventArgs args = Interlocked.Exchange(ref _cachedSendArgs, null) ?? new RegisteredOperationEventArgs();

            (ValueTask<int> task, IntPtr requestContext, IntPtr buffersPtr) = args.Prepare(this, isSend: true, memory);
            try
            {
                lock (_requestQueue)
                {
                    Interop.Rio.Send(_requestQueue, buffersPtr, memory.Length, 0, requestContext);
                }
            }
            catch (Exception ex)
            {
                args.Complete(ex, 0);
            }

            return task;
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> memory)
        {
            RegisteredOperationEventArgs args = Interlocked.Exchange(ref _cachedRecvArgs, null) ?? new RegisteredOperationEventArgs();

            (ValueTask<int> task, IntPtr requestContext, IntPtr buffersPtr) = args.Prepare(this, isSend: false, memory);
            try
            {
                lock (_requestQueue)
                {
                    Interop.Rio.Receive(_requestQueue, buffersPtr, 1, 0, requestContext);
                }
            }
            catch (Exception ex)
            {
                args.Complete(ex, 0);
            }

            return task;
        }

        public ValueTask<int> ReceiveAsync(ReadOnlySpan<Memory<byte>> memory)
        {
            RegisteredOperationEventArgs args = Interlocked.Exchange(ref _cachedRecvArgs, null) ?? new RegisteredOperationEventArgs();

            (ValueTask<int> task, IntPtr requestContext, IntPtr buffersPtr) = args.Prepare(this, isSend: false, memory);
            try
            {
                lock (_requestQueue)
                {
                    Interop.Rio.Receive(_requestQueue, buffersPtr, memory.Length, 0, requestContext);
                }
            }
            catch (Exception ex)
            {
                args.Complete(ex, 0);
            }

            return task;
        }

        public static Socket CreateRegisterableSocket(AddressFamily family, SocketType socketType, ProtocolType protocolType)
        {
            Interop.Rio.Init();

            SafeSocketHandle socketHandle = Interop.Rio.CreateRegisterableSocket((int)family, (int)socketType, (int)protocolType);

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
