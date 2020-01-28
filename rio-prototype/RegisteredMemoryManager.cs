using System;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace rio_prototype
{
    /// <summary>
    /// A manager for registered memory.
    /// </summary>
    internal sealed unsafe class RegisteredMemoryManager : MemoryManager<byte>
    {
        private IntPtr _rioBuffer;
        private void* _ptr;
        private int _len;

        public IntPtr RioBufferId => _rioBuffer;

        public RegisteredMemoryManager(int minimumLength)
        {
            Interop.Kernel32.GetSystemInfo(out Interop.Kernel32.SYSTEM_INFO si);

            long size = minimumLength + si.dwAllocationGranularity - 1;
            size -= size % si.dwAllocationGranularity;
            minimumLength = (int)size;

            _ptr = Interop.Kernel32.VirtualAlloc(null, new UIntPtr((uint)minimumLength), Interop.Kernel32.MEM_COMMIT | Interop.Kernel32.MEM_RESERVE, Interop.Kernel32.PAGE_READWRITE);
            if (_ptr == null) throw new Win32Exception();

            _len = minimumLength;

            try
            {
                _rioBuffer = Interop.Rio.RegisterBuffer(new IntPtr(_ptr), _len);
            }
            catch
            {
                Interop.Kernel32.VirtualFree(_ptr, new UIntPtr((uint)_len), Interop.Kernel32.MEM_RELEASE);
                throw;
            }

            GC.AddMemoryPressure(minimumLength);
        }

        ~RegisteredMemoryManager()
        {
            Dispose(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (_ptr != null)
            {
                Interop.Rio.DeregisterBuffer(_rioBuffer);
                _rioBuffer = IntPtr.Zero;

                if (!Interop.Kernel32.VirtualFree(_ptr, new UIntPtr((uint)_len), Interop.Kernel32.MEM_RELEASE))
                {
                    throw new Win32Exception();
                }
                _ptr = null;

                GC.RemoveMemoryPressure(_len);
                if(disposing) GC.SuppressFinalize(this);
            }
        }

        public override Span<byte> GetSpan()
        {
            if (_ptr == null) throw new ObjectDisposedException(nameof(RegisteredMemoryManager));
            return new Span<byte>(_ptr, _len);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if (_ptr == null) throw new ObjectDisposedException(nameof(RegisteredMemoryManager));
            if (elementIndex >= _len) throw new ArgumentOutOfRangeException(nameof(elementIndex));
            return new MemoryHandle((byte*)_ptr + elementIndex, pinnable: this);
        }

        public override void Unpin()
        {
            if (_ptr == null) throw new ObjectDisposedException(nameof(RegisteredMemoryManager));
        }
    }
}
