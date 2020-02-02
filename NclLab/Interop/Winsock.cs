using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NclLab.Interop
{
    internal static class Winsock
    {
        private const int WSA_FLAG_OVERLAPPED = 0x01;
        private const int WSA_FLAG_NO_HANDLE_INHERIT = 0x80;
        private const int WSA_FLAG_REGISTERED_IO = 0x100;

        public static SafeSocketHandle CreateRegisterableSocket(int af, int type, int protocol)
        {
            SafeSocketHandle handle = WSASocketW(af, type, protocol, IntPtr.Zero, 0, WSA_FLAG_OVERLAPPED | WSA_FLAG_NO_HANDLE_INHERIT | WSA_FLAG_REGISTERED_IO);
            if (handle.IsInvalid) throw new SocketException();
            return handle;
        }

        [DllImport("ws2_32.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true, SetLastError = true)]
        private static extern SafeSocketHandle WSASocketW(int af, int type, int protocol, IntPtr lpProtocolInfo, uint g, uint flags);
    }
}
