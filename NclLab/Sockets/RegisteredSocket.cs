using System;
using System.Buffers;
using System.Diagnostics;
using System.Linq.Expressions;
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
        internal readonly Socket _socket;
        private readonly Interop.SafeRioRequestQueueHandle _requestQueue;
        private uint _currentSendQueueSize = 1, _currentReceiveQueueSize = 1;

        /// <summary>
        /// Registers a socket against a multiplexer.
        /// </summary>
        /// <param name="multiplexer">The multiplexer to register a socket with.</param>
        /// <param name="socket">The socket to register. Must have been created using <see cref="CreateRegisterableSocket(AddressFamily, SocketType, ProtocolType)"/>.</param>
        public RegisteredSocket(RegisteredMultiplexer multiplexer, Socket socket)
        {
            _socket = socket;
            _requestQueue = multiplexer.RegisterSocket(socket.SafeHandle);
        }

        public void Dispose()
        {
            _requestQueue.Dispose();
        }

        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> memory, RegisteredOperationContext context)
        {
            (ValueTask<int> task, IntPtr requestContext, IntPtr buffersPtr) = context.Prepare(this, memory);
            try
            {
                lock (_requestQueue)
                {
                    while (true)
                    {
                        SocketError err = Interop.Rio.Send(_requestQueue, buffersPtr, bufferCount: 1, remoteAddress: IntPtr.Zero, flags: 0, requestContext);
                        switch (err)
                        {
                            case SocketError.Success:
                            case SocketError.IOPending:
                                return task;
                            case SocketError.NoBufferSpaceAvailable:
                                ResizeSendQueue();
                                continue;
                            default:
                                context.Complete(new SocketException((int)err), transferred: 0);
                                return task;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                context.Complete(ex, transferred: 0);
                return task;
            }
        }

        public ValueTask<int> SendAsync(ReadOnlySpan<ReadOnlyMemory<byte>> memory, RegisteredOperationContext context)
        {
            (ValueTask<int> task, IntPtr requestContext, IntPtr buffersPtr) = context.Prepare(this, memory);
            try
            {
                lock (_requestQueue)
                {
                    while (true)
                    {
                        SocketError err = Interop.Rio.Send(_requestQueue, buffersPtr, memory.Length, remoteAddress: IntPtr.Zero, flags: 0, requestContext);
                        switch (err)
                        {
                            case SocketError.Success:
                            case SocketError.IOPending:
                                return task;
                            case SocketError.NoBufferSpaceAvailable:
                                ResizeSendQueue();
                                continue;
                            default:
                                context.Complete(new SocketException((int)err), transferred: 0);
                                return task;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                context.Complete(ex, transferred: 0);
                return task;
            }
        }

        public ValueTask<int> SendToAsync(ReadOnlyMemory<byte> memory, RegisteredEndPoint remoteEndPoint, RegisteredOperationContext context)
        {
            (ValueTask<int> task, IntPtr requestContext, IntPtr buffersPtr, IntPtr remoteEndPointPtr) = context.Prepare(this, memory, remoteEndPoint);
            try
            {
                lock (_requestQueue)
                {
                    while (true)
                    {
                        SocketError err = Interop.Rio.Send(_requestQueue, buffersPtr, bufferCount: 1, remoteEndPointPtr, flags: 0, requestContext);
                        switch (err)
                        {
                            case SocketError.Success:
                            case SocketError.IOPending:
                                return task;
                            case SocketError.NoBufferSpaceAvailable:
                                ResizeSendQueue();
                                continue;
                            default:
                                context.Complete(new SocketException((int)err), transferred: 0);
                                return task;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                context.Complete(ex, transferred: 0);
                return task;
            }
        }

        public ValueTask<int> SendToAsync(ReadOnlySpan<ReadOnlyMemory<byte>> memory, RegisteredEndPoint remoteEndPoint, RegisteredOperationContext context)
        {
            (ValueTask<int> task, IntPtr requestContext, IntPtr buffersPtr, IntPtr remoteEndPointPtr) = context.Prepare(this, memory, remoteEndPoint);
            try
            {
                lock (_requestQueue)
                {
                    while (true)
                    {
                        SocketError err = Interop.Rio.Send(_requestQueue, buffersPtr, memory.Length, remoteEndPointPtr, flags: 0, requestContext);
                        switch (err)
                        {
                            case SocketError.Success:
                            case SocketError.IOPending:
                                return task;
                            case SocketError.NoBufferSpaceAvailable:
                                ResizeSendQueue();
                                continue;
                            default:
                                context.Complete(new SocketException((int)err), transferred: 0);
                                return task;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                context.Complete(ex, transferred: 0);
                return task;
            }
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> memory, RegisteredOperationContext context)
        {
            (ValueTask<int> task, IntPtr requestContext, IntPtr buffersPtr) = context.Prepare(this, memory);
            try
            {
                lock (_requestQueue)
                {
                    while (true)
                    {
                        SocketError err = Interop.Rio.Receive(_requestQueue, buffersPtr, bufferCount: 1, remoteAddress: IntPtr.Zero, controlContext: IntPtr.Zero, flagsOut: IntPtr.Zero, flags: 0, requestContext);
                        switch (err)
                        {
                            case SocketError.Success:
                            case SocketError.IOPending:
                                return task;
                            case SocketError.NoBufferSpaceAvailable:
                                ResizeReceiveQueue();
                                continue;
                            default:
                                context.Complete(new SocketException((int)err), transferred: 0);
                                return task;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                context.Complete(ex, transferred: 0);
                return task;
            }
        }

        public ValueTask<int> ReceiveAsync(ReadOnlySpan<Memory<byte>> memory, RegisteredOperationContext context)
        {
            (ValueTask<int> task, IntPtr requestContext, IntPtr buffersPtr) = context.Prepare(this, memory);
            try
            {
                lock (_requestQueue)
                {
                    while (true)
                    {
                        SocketError err = Interop.Rio.Receive(_requestQueue, buffersPtr, memory.Length, remoteAddress: IntPtr.Zero, controlContext: IntPtr.Zero, flagsOut: IntPtr.Zero, flags: 0, requestContext);
                        switch (err)
                        {
                            case SocketError.Success:
                            case SocketError.IOPending:
                                return task;
                            case SocketError.NoBufferSpaceAvailable:
                                ResizeReceiveQueue();
                                continue;
                            default:
                                context.Complete(new SocketException((int)err), transferred: 0);
                                return task;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                context.Complete(ex, transferred: 0);
                return task;
            }
        }

        public ValueTask<int> ReceiveFromAsync(Memory<byte> memory, RegisteredEndPoint remoteEndPoint, RegisteredOperationContext context)
        {
            (ValueTask<int> task, IntPtr requestContext, IntPtr buffersPtr, IntPtr remoteEndPointPtr) = context.Prepare(this, memory, remoteEndPoint);
            try
            {
                lock (_requestQueue)
                {
                    while (true)
                    {
                        SocketError err = Interop.Rio.Receive(_requestQueue, buffersPtr, bufferCount: 1, remoteEndPointPtr, controlContext: IntPtr.Zero, flagsOut: IntPtr.Zero, flags: 0, requestContext);
                        switch (err)
                        {
                            case SocketError.Success:
                            case SocketError.IOPending:
                                return task;
                            case SocketError.NoBufferSpaceAvailable:
                                ResizeReceiveQueue();
                                continue;
                            default:
                                context.Complete(new SocketException((int)err), transferred: 0);
                                return task;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                context.Complete(ex, transferred: 0);
                return task;
            }
        }

        public ValueTask<int> ReceiveFromAsync(ReadOnlySpan<Memory<byte>> memory, RegisteredEndPoint remoteEndPoint, RegisteredOperationContext context)
        {
            (ValueTask<int> task, IntPtr requestContext, IntPtr buffersPtr, IntPtr remoteEndPointPtr) = context.Prepare(this, memory, remoteEndPoint);
            try
            {
                lock (_requestQueue)
                {
                    while (true)
                    {
                        SocketError err = Interop.Rio.Receive(_requestQueue, buffersPtr, memory.Length, remoteEndPointPtr, controlContext: IntPtr.Zero, flagsOut: IntPtr.Zero, flags: 0, requestContext);
                        switch (err)
                        {
                            case SocketError.Success:
                            case SocketError.IOPending:
                                return task;
                            case SocketError.NoBufferSpaceAvailable:
                                ResizeReceiveQueue();
                                continue;
                            default:
                                context.Complete(new SocketException((int)err), transferred: 0);
                                return task;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                context.Complete(ex, transferred: 0);
                return task;
            }
        }

        private void ResizeSendQueue()
        {
            Debug.Assert(Monitor.IsEntered(_requestQueue));
            ResizeRequestQueue(_currentReceiveQueueSize, _currentSendQueueSize * 2);
        }

        private void ResizeReceiveQueue()
        {
            Debug.Assert(Monitor.IsEntered(_requestQueue));
            ResizeRequestQueue(_currentReceiveQueueSize * 2, _currentSendQueueSize);
        }

        private void ResizeRequestQueue(uint newReceiveQueueSize, uint newSendQueueSize)
        {
            Debug.Assert(Monitor.IsEntered(_requestQueue));
            Interop.Rio.ResizeRequestQueue(_requestQueue, newReceiveQueueSize, newSendQueueSize);
            _currentReceiveQueueSize = newReceiveQueueSize;
            _currentSendQueueSize = newSendQueueSize;
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
