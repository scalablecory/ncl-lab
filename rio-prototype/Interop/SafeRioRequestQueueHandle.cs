using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace rio_prototype.Interop
{
    internal sealed class SafeRioRequestQueueHandle : SafeHandle
    {
        private SafeSocketHandle _socket;
        private SafeRioCompletionQueueHandle _completionQueue;

        public SafeRioRequestQueueHandle(IntPtr existingHandle, bool ownsHandle) : base(IntPtr.Zero, ownsHandle)
        {
            SetHandle(existingHandle);
        }

        private SafeRioRequestQueueHandle() : base(IntPtr.Zero, true)
        {
        }

        public void SetDependencies(SafeSocketHandle socket, SafeRioCompletionQueueHandle completionQueue)
        {
            bool socketAddRefSuccess = false;
            bool completionQueueAddRefSuccess = false;

            try
            {
                socket.DangerousAddRef(ref socketAddRefSuccess);
                completionQueue.DangerousAddRef(ref completionQueueAddRefSuccess);
            }
            catch
            {
                if (socketAddRefSuccess) socket.DangerousRelease();
                if (completionQueueAddRefSuccess) completionQueue.DangerousRelease();
                throw;
            }

            _socket = socket;
            _completionQueue = completionQueue;
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            // There is no deallocation needed for this handle; it is deallocated as part of the socket.
            handle = IntPtr.Zero;

            _socket.DangerousRelease();
            _completionQueue.DangerousRelease();

            return true;
        }
    }
}
