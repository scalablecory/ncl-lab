using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace NclLab.Interop
{
    internal unsafe static class Kernel32
    {
        public const int ERROR_NOT_FOUND = 0x490;
        public const int MEM_COMMIT = 0x00001000;
        public const int MEM_RESERVE = 0x00002000;
        public const int MEM_RELEASE = 0x00008000;
        public const int PAGE_READWRITE = 0x04;

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern void* VirtualAlloc(void* lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool VirtualFree(void* lpAddress, UIntPtr dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", ExactSpelling =  true, SetLastError = true)]
        public static extern bool CancelIoEx(SafeHandle handle, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern void GetSystemInfo(out SYSTEM_INFO lpSystemInfo);

        public struct SYSTEM_INFO
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
