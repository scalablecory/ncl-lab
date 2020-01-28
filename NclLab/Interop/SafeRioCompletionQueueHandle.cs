using Microsoft.Win32.SafeHandles;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NclLab.Interop
{
    internal sealed class SafeRioCompletionQueueHandle : SafeHandle
    {
        private SafeWaitHandle _waitHandle;

        public SafeRioCompletionQueueHandle(IntPtr existingHandle, bool ownsHandle) : base(IntPtr.Zero, ownsHandle)
        {
            SetHandle(existingHandle);
        }

        private SafeRioCompletionQueueHandle() : base(IntPtr.Zero, true)
        {
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            Interop.Rio.CloseCompletionQueue(handle);
            handle = IntPtr.Zero;
            _waitHandle.DangerousRelease();
            return true;
        }

        public void SetDependencies(SafeWaitHandle waitHandle)
        {
            bool darSuccess = false;
            waitHandle.DangerousAddRef(ref darSuccess);
            Debug.Assert(darSuccess == true);

            _waitHandle = waitHandle;
        }
    }
}
