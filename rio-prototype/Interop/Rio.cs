using Microsoft.Win32.SafeHandles;
using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace rio_prototype.Interop
{
    internal static class Rio
    {
        private const int RIO_CORRUPT_CQ = -1;
        private const int RIO_EVENT_COMPLETION = 1;
        private const int SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER = unchecked((int)0xC8000024);
        private const int WSA_FLAG_OVERLAPPED = 0x01;
        private const int WSA_FLAG_REGISTERED_IO = 0x100;

        private static bool s_init;
        private static RIOSendEx s_rioSendEx;
        private static RIOReceiveEx s_rioReceiveEx;
        private static RIODequeueCompletion s_rioDequeueCompletion;
        private static RIONotify s_rioNotify;
        private static RIORegisterBuffer s_rioRegisterBuffer;
        private static RIODeregisterBuffer s_rioDeregisterBuffer;
        private static RIOResizeCompletionQueue s_rioResizeCompletionQueue;
        private static RIOCreateRequestQueue s_rioCreateRequestQueue;
        private static RIOResizeRequestQueue s_rioResizeRequestQueue;
        private static RIOCreateCompletionQueue s_rioCreateCompletionQueue;
        private static RIOCloseCompletionQueue s_rioCloseCompletionQueue;

        public static IntPtr RegisterBuffer(IntPtr buffer, int bufferLength)
        {
            Debug.Assert(buffer != IntPtr.Zero);
            Debug.Assert(bufferLength > 0);

            IntPtr bufferId = s_rioRegisterBuffer(buffer, bufferLength);

            if (bufferId == new IntPtr(-1))
            {
                throw new SocketException();
            }

            return bufferId;
        }

        public static void DeregisterBuffer(IntPtr rioBufferId)
        {
            Debug.Assert(rioBufferId != IntPtr.Zero);
            s_rioDeregisterBuffer(rioBufferId);
        }

        public static SafeRioCompletionQueueHandle CreateCompletionQueue(uint queueSize, SafeWaitHandle waitHandle)
        {
            Debug.Assert(queueSize > 0);
            Debug.Assert(waitHandle != null);

            var rnc = new RIO_NOTIFICATION_COMPLETION
            {
                Type = RIO_EVENT_COMPLETION,
                EventHandle = waitHandle.DangerousGetHandle(),
                NotifyReset = 0,
                Padding = IntPtr.Zero
            };

            SafeRioCompletionQueueHandle handle = s_rioCreateCompletionQueue(queueSize, ref rnc);
            if (handle.IsInvalid) throw new SocketException();

            handle.SetDependencies(waitHandle);
            return handle;
        }

        public static void ResizeCompletionQueue(SafeRioCompletionQueueHandle queueHandle, uint queueSize)
        {
            Debug.Assert(!queueHandle.IsInvalid);
            if (!s_rioResizeCompletionQueue(queueHandle, queueSize))
                throw new SocketException();
        }

        public static void CloseCompletionQueue(IntPtr queueHandle)
        {
            Debug.Assert(queueHandle != IntPtr.Zero);
            s_rioCloseCompletionQueue(queueHandle);
        }

        public static void Notify(SafeRioCompletionQueueHandle queueHandle)
        {
            Debug.Assert(!queueHandle.IsInvalid);

            int res = s_rioNotify(queueHandle);
            if (res != (int)SocketError.Success)
            {
                throw new SocketException(res);
            }
        }

        public static SafeRioRequestQueueHandle CreateRequestQueue(SafeSocketHandle socket, SafeRioCompletionQueueHandle completionQueue, IntPtr context, uint maxOutstandingReceive, uint maxReceiveDataBuffers, uint maxOutstandingSend, uint maxSendDataBuffers)
        {
            Debug.Assert(!socket.IsInvalid);
            Debug.Assert(!completionQueue.IsInvalid);

            SafeRioRequestQueueHandle queue = s_rioCreateRequestQueue(socket, maxOutstandingReceive, maxReceiveDataBuffers, maxOutstandingSend, maxSendDataBuffers, completionQueue, completionQueue, context);
            if (queue.IsInvalid) throw new SocketException();

            queue.SetDependencies(socket, completionQueue);
            return queue;
        }

        public static void ResizeRequestQueue(SafeRioRequestQueueHandle queue, uint maxOutstandingReceive, uint maxOutstandingSend)
        {
            Debug.Assert(!queue.IsInvalid);

            if (!s_rioResizeRequestQueue(queue, maxOutstandingReceive, maxOutstandingSend))
            {
                throw new SocketException();
            }
        }

        public static int DequeueCompletions(SafeRioCompletionQueueHandle queue, Span<RIORESULT> results)
        {
            Debug.Assert(!queue.IsInvalid);

            int res = s_rioDequeueCompletion(queue, ref MemoryMarshal.GetReference(results), results.Length);
            if (res == RIO_CORRUPT_CQ) throw new Exception("RIO completion queue is corrupt.");

            return res;
        }

        public static SocketError Send(SafeRioRequestQueueHandle queue, IntPtr buffers, int bufferCount, IntPtr remoteAddress, uint flags, IntPtr requestContext)
        {
            Debug.Assert(!queue.IsInvalid);
            return s_rioSendEx(queue, buffers, bufferCount, IntPtr.Zero, remoteAddress, IntPtr.Zero, IntPtr.Zero, flags, requestContext) ? SocketError.Success : (SocketError)Marshal.GetLastWin32Error();
        }

        public static SocketError Receive(SafeRioRequestQueueHandle queue, IntPtr buffers, int bufferCount, IntPtr remoteAddress, IntPtr controlContext, IntPtr flagsOut, uint flags, IntPtr requestContext)
        {
            Debug.Assert(!queue.IsInvalid);

            return s_rioReceiveEx(queue, buffers, bufferCount, IntPtr.Zero, remoteAddress, controlContext, flagsOut, flags, requestContext) ? SocketError.Success : (SocketError)Marshal.GetLastWin32Error();
        }

        public static SafeSocketHandle CreateRegisterableSocket(int af, int type, int protocol)
        {
            SafeSocketHandle handle = WSASocketW(af, type, protocol, IntPtr.Zero, 0, WSA_FLAG_OVERLAPPED | WSA_FLAG_REGISTERED_IO);
            if (handle.IsInvalid) throw new SocketException();
            return handle;
        }

        [DllImport("ws2_32.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true, SetLastError = true)]
        private static extern SafeSocketHandle WSASocketW(int af, int type, int protocol, IntPtr lpProtocolInfo, uint g, uint flags);

        public static void Init()
        {
            if (!s_init)
            {
                InitSlow();
            }

            static unsafe void InitSlow()
            {
                var wsaid_multiple_rio = new byte[] { 0x81, 0xe0, 0x09, 0x85, 0xdd, 0x96, 0x05, 0x40, 0xb1, 0x65, 0x9e, 0x2e, 0xe8, 0xc7, 0x9e, 0x3f };
                var outbuf = new byte[sizeof(RIO_EXTENSION_FUNCTION_TABLE)];
                int outlen;

                using (Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    outlen = sock.IOControl(SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER, wsaid_multiple_rio, outbuf);
                }

                Debug.Assert(outlen == outbuf.Length);

                ref readonly RIO_EXTENSION_FUNCTION_TABLE table = ref MemoryMarshal.AsRef<RIO_EXTENSION_FUNCTION_TABLE>(outbuf);

                s_rioRegisterBuffer = Marshal.GetDelegateForFunctionPointer<RIORegisterBuffer>(table.RIORegisterBuffer);
                s_rioDeregisterBuffer = Marshal.GetDelegateForFunctionPointer<RIODeregisterBuffer>(table.RIODeregisterBuffer);
                s_rioCreateCompletionQueue = Marshal.GetDelegateForFunctionPointer<RIOCreateCompletionQueue>(table.RIOCreateCompletionQueue);
                s_rioResizeCompletionQueue = Marshal.GetDelegateForFunctionPointer<RIOResizeCompletionQueue>(table.RIOResizeCompletionQueue);
                s_rioCloseCompletionQueue = Marshal.GetDelegateForFunctionPointer<RIOCloseCompletionQueue>(table.RIOCloseCompletionQueue);
                s_rioNotify = Marshal.GetDelegateForFunctionPointer<RIONotify>(table.RIONotify);
                s_rioCreateRequestQueue = Marshal.GetDelegateForFunctionPointer<RIOCreateRequestQueue>(table.RIOCreateRequestQueue);
                s_rioResizeRequestQueue = Marshal.GetDelegateForFunctionPointer<RIOResizeRequestQueue>(table.RIOResizeRequestQueue);
                s_rioDequeueCompletion = Marshal.GetDelegateForFunctionPointer<RIODequeueCompletion>(table.RIODequeueCompletion);
                s_rioSendEx = Marshal.GetDelegateForFunctionPointer<RIOSendEx>(table.RIOSendEx);
                s_rioReceiveEx = Marshal.GetDelegateForFunctionPointer<RIOReceiveEx>(table.RIOReceiveEx);
                Volatile.Write(ref s_init, true);
            }
        }

        private struct RIO_EXTENSION_FUNCTION_TABLE
        {
            public uint cbSize;
            public IntPtr RIOReceive;
            public IntPtr RIOReceiveEx;
            public IntPtr RIOSend;
            public IntPtr RIOSendEx;
            public IntPtr RIOCloseCompletionQueue;
            public IntPtr RIOCreateCompletionQueue;
            public IntPtr RIOCreateRequestQueue;
            public IntPtr RIODequeueCompletion;
            public IntPtr RIODeregisterBuffer;
            public IntPtr RIONotify;
            public IntPtr RIORegisterBuffer;
            public IntPtr RIOResizeCompletionQueue;
            public IntPtr RIOResizeRequestQueue;
        }

        public struct RIO_NOTIFICATION_COMPLETION
        {
            public int Type;
            public IntPtr EventHandle;
            public int NotifyReset; // win32 BOOL; int to make blittable.
            public IntPtr Padding;
        }

        public struct RIORESULT
        {
            public int Status;
            public int BytesTransferred;
            public long SocketContext;
            public long RequestContext;
        }

        public struct RIO_BUF
        {
            public IntPtr BufferId;
            public int Offset;
            public int Length;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate IntPtr RIORegisterBuffer(IntPtr DataBuffer, int DataLength);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void RIODeregisterBuffer(IntPtr rioBufferId);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate SafeRioCompletionQueueHandle RIOCreateCompletionQueue(uint QueueSize, ref RIO_NOTIFICATION_COMPLETION NotificationCompletion);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate bool RIOResizeCompletionQueue(SafeRioCompletionQueueHandle CQ, uint QueueSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int RIONotify(SafeRioCompletionQueueHandle CQ);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int RIODequeueCompletion(SafeRioCompletionQueueHandle CQ, ref RIORESULT results, int resultCount);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void RIOCloseCompletionQueue(IntPtr CQ);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate SafeRioRequestQueueHandle RIOCreateRequestQueue(SafeSocketHandle Socket, uint MaxOutstandingReceive, uint MaxReceiveDataBuffers, uint MaxOutstandingSend, uint MaxSendDataBuffers, SafeRioCompletionQueueHandle ReceiveCQ, SafeRioCompletionQueueHandle SendCQ, IntPtr SocketContext);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate bool RIOResizeRequestQueue(SafeRioRequestQueueHandle RQ, uint MaxOutstandingReceive, uint MaxOutstandingSend);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate bool RIOSendEx(SafeRioRequestQueueHandle SocketQueue, IntPtr pData, int DataBufferCount, IntPtr pLocalAddress, IntPtr pRemoteAddress, IntPtr pControlContext, IntPtr pFlags, uint Flags, IntPtr RequestContext);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate bool RIOReceiveEx(SafeRioRequestQueueHandle SocketQueue, IntPtr pData, int DataBufferCount, IntPtr pLocalAddress, IntPtr pRemoteAddress, IntPtr pControlContext, IntPtr pFlags, uint Flags, IntPtr RequestContext);
    }
}
