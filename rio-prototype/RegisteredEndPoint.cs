using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;

namespace rio_prototype
{
    /// <summary>
    /// A mutable, registered EndPoint.
    /// </summary>
    public sealed class RegisteredEndPoint : IDisposable, IComparable<RegisteredEndPoint>, IEquatable<RegisteredEndPoint>, IComparable<EndPoint>, IEquatable<EndPoint>
    {
        const int SIZEOF_SOCKADDR_INET = 28; // sizeof(SOCKADDR_INET)

        private readonly IMemoryOwner<byte> _memoryOwner;

        public EndPoint EndPoint
        {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        internal Memory<byte> Memory => _memoryOwner.Memory;

        public RegisteredEndPoint(MemoryPool<byte> memoryPool)
        {
            if (memoryPool == null) throw new ArgumentNullException(nameof(memoryPool));
            _memoryOwner = memoryPool.Rent(SIZEOF_SOCKADDR_INET);
        }

        public void Dispose()
        {
            _memoryOwner.Dispose();
        }

        public int CompareTo(RegisteredEndPoint other)
        {
            throw new NotImplementedException();
        }

        public int CompareTo(EndPoint other)
        {
            throw new NotImplementedException();
        }

        public bool Equals(RegisteredEndPoint other)
        {
            throw new NotImplementedException();
        }

        public bool Equals(EndPoint other)
        {
            throw new NotImplementedException();
        }

        public override bool Equals(object obj)
        {
            return obj switch
            {
                RegisteredEndPoint rsep => Equals(rsep),
                EndPoint ep => Equals(ep),
                _ => false
            };
        }

        public override int GetHashCode()
        {
            throw new NotImplementedException();
        }
    }
}
