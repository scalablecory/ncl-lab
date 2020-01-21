using System.Buffers;
using System.Numerics;

namespace rio_prototype
{
    public sealed class RegisteredMemoryPool : MemoryPool<byte>
    {
        private const int MinBufferSizeConst = 1 << 6;
        private const int MaxBufferSizeConst = 1 << 16;

        private readonly RegisteredMemoryFreeList[] _freeLists = new RegisteredMemoryFreeList[]
        {
            new RegisteredMemoryFreeList(1 << 16),
            new RegisteredMemoryFreeList(1 << 15),
            new RegisteredMemoryFreeList(1 << 14),
            new RegisteredMemoryFreeList(1 << 13),
            new RegisteredMemoryFreeList(1 << 12),
            new RegisteredMemoryFreeList(1 << 11),
            new RegisteredMemoryFreeList(1 << 10),
            new RegisteredMemoryFreeList(1 << 9),
            new RegisteredMemoryFreeList(1 << 8),
            new RegisteredMemoryFreeList(1 << 7),
            new RegisteredMemoryFreeList(MinBufferSizeConst)
        };

        public override int MaxBufferSize => MaxBufferSizeConst;

        public RegisteredMemoryPool()
        {
            Interop.Rio.Init();
        }

        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            if (minBufferSize > 0)
            {
                if (minBufferSize > MinBufferSizeConst)
                {
                    if (minBufferSize <= MaxBufferSizeConst)
                    {
                        uint idx = (uint)BitOperations.LeadingZeroCount((uint)minBufferSize) - 15u;
                        return _freeLists[idx].GetBlock();
                    }
                }
                else
                {
                    // Minimum size 
                    return _freeLists[10].GetBlock();
                }
            }
            else
            {
                // Default to 4KiB
                return _freeLists[4].GetBlock();
            }

            // Nuclear option: if more than maximum, go unpooled.
            return new RegisteredMemoryManager(minBufferSize);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (RegisteredMemoryFreeList freeList in _freeLists)
                {
                    freeList.Dispose();
                }
            }
        }
    }
}
