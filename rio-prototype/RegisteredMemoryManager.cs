using System;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace rio_prototype
{
    internal sealed unsafe class RegisteredMemoryManager : MemoryManager<byte>
    {
        private IntPtr _rioBuffer;
        private void* _ptr;
        private int _len;

        public IntPtr RioBufferId => _rioBuffer;

        public RegisteredMemoryManager(int minimumLength)
        {
            GetSystemInfo(out SYSTEM_INFO si);

            long size = minimumLength + si.dwAllocationGranularity - 1;
            size -= size % si.dwAllocationGranularity;
            minimumLength = (int)size;

            _ptr = VirtualAlloc(null, new UIntPtr((uint)minimumLength), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (_ptr == null) throw new Win32Exception();

            _len = minimumLength;
            _rioBuffer = Interop.Rio.RegisterBuffer(new IntPtr(_ptr), _len);

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

                if (!VirtualFree(_ptr, new UIntPtr((uint)_len), MEM_RELEASE))
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

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern void* VirtualAlloc(void* lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool VirtualFree(void* lpAddress, UIntPtr dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern void GetSystemInfo(out SYSTEM_INFO lpSystemInfo);

        private const int MEM_COMMIT = 0x00001000;
        private const int MEM_RESERVE = 0x00002000;
        private const int MEM_RELEASE = 0x00008000;
        private const int PAGE_READWRITE = 0x04;

        struct SYSTEM_INFO
        {
            public uint dwOemId;
            public uint dwPageSize;
            public IntPtr lpMinimumApplicationAddress;
            public IntPtr lpMaximumApplicationAddress;
            public UIntPtr dwActiveProcessorMask;
            public uint dwNumberOfProcessors;
            public uint dwProcessorType;
            public uint dwAllocationGranularity;
            public short wProcessorLevel;
            public short wProcessorRevision;
        }
    }
}
