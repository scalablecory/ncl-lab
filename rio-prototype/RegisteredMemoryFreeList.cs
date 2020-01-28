using System;
using System.Buffers;
using System.Threading;

namespace rio_prototype
{
    /// <summary>
    /// A thread-safe memory pool for specific block sizes.
    /// </summary>
    internal sealed class RegisteredMemoryFreeList : IDisposable
    {
        private OwnedMemory _free;
        private readonly uint _blockSize;
        private volatile uint _nextAllocSize;

        public RegisteredMemoryFreeList(int blockSize)
        {
            _blockSize = (uint)blockSize;
            _nextAllocSize = _blockSize * 10;
        }
        public void Dispose()
        {
            while (Pop() != null) ;
        }

        public IMemoryOwner<byte> GetBlock()
        {
            OwnedMemory memory = Pop();
            if (memory != null)
            {
                memory.OwningList = this;
                return memory;
            }
            return GetBlockSlow();
        }

        private IMemoryOwner<byte> GetBlockSlow()
        {
            uint allocSize = _nextAllocSize;
            _nextAllocSize = Math.Min(allocSize * 3u / 2u, 4 * 1024 * 1024);

            Memory<byte> memory = new RegisteredMemoryManager((int)allocSize).Memory;

            for (int i = (int)_blockSize; i < memory.Length; i += (int)_blockSize)
            {
                Push(new OwnedMemory(memory.Slice(i, (int)_blockSize)));
            }

            var ret = new OwnedMemory(memory.Slice(0, (int)_blockSize));
            ret.OwningList = this;
            return ret;
        }

        private OwnedMemory Pop()
        {
            OwnedMemory current = _free, cmp;
            do
            {
                if (current == null)
                {
                    return current;
                }

                cmp = current;
            } while ((current = Interlocked.CompareExchange(ref _free, current.Next, current)) != cmp);

            current.Next = null;
            return current;
        }

        private void Push(OwnedMemory memory)
        {
            OwnedMemory current = _free, cmp;
            do
            {
                memory.Next = current;
                cmp = current;
            } while ((current = Interlocked.CompareExchange(ref _free, memory, current)) != cmp);
        }

        sealed class OwnedMemory : IMemoryOwner<byte>
        {
            public RegisteredMemoryFreeList OwningList;
            public OwnedMemory Next;

            public Memory<byte> Memory { get; }

            public OwnedMemory(Memory<Byte> memory)
            {
                Memory = memory;
            }

            public void Dispose()
            {
                RegisteredMemoryFreeList owningList = OwningList;
                OwningList = null;
                owningList.Push(this);
            }
        }
    }
}
