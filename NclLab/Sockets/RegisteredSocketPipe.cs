using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace NclLab.Sockets
{
    public sealed class RegisteredSocketPipe : IDuplexPipe
    {
        public PipeReader Input { get; }
        public PipeWriter Output { get; }

        public RegisteredSocketPipe(RegisteredMemoryPool pool, RegisteredSocket socket, long? maxSendBufferSize, long? maxReceiveBufferSize)
        {
            var state = new State(pool, socket, maxSendBufferSize, maxReceiveBufferSize);
            Input = new Reader(state);
            Output = state;
        }

        sealed class State : PipeWriter
        {
            private const int MinimumSendBufferLength = 1024;

            readonly RegisteredMemoryPool _pool;
            readonly RegisteredSocket _socket;

            readonly Queue<SendOperation> _sendOperations = new Queue<SendOperation>();
            SendOperation _sendOperationCacheHead;
            IMemoryOwner<byte> _currentOwner;
            int _currentSendOffset, _currentWriteOffset;
            long _totalSendBytesQueued;
            readonly int _maxSendBytesQueued, _resumeSendBytesQueued;

            readonly object _receiveSyncObj = new object();
            ReadOperation _receiveStart, _receiveEnd;
            int _receiveEndAvailable;
            long _totalReceiveBytesAvailable;
            readonly int _maxReceiveBytesAvailable, _resumeReceiveBytesAvailable, _receiveSegmentLength;
            readonly ResettableValueTaskSource _pauseReceiveTaskSource = new ResettableValueTaskSource();
            readonly ResettableValueTaskSource _receiveDataAvailableTaskSource = new ResettableValueTaskSource();

            public State(RegisteredMemoryPool pool, RegisteredSocket socket, long? maxSendBufferSize, long? maxReceiveBufferSize)
            {
                _pool = pool;
                _socket = socket;

                int maxSendBytesQueuedActual = (int)Math.Clamp(maxSendBufferSize.GetValueOrDefault(), 512, int.MaxValue);
                _maxSendBytesQueued = maxSendBytesQueuedActual;
                _resumeSendBytesQueued = maxSendBytesQueuedActual >> 1;

                int maxReceiveBytesQueuedActual = (int)Math.Clamp(maxReceiveBufferSize.GetValueOrDefault(), 512, int.MaxValue);
                _maxReceiveBytesAvailable = maxReceiveBytesQueuedActual;
                _resumeReceiveBytesAvailable = maxReceiveBytesQueuedActual >> 2;
                _receiveSegmentLength = maxReceiveBytesQueuedActual >> 2;
            }

            public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

            public override Memory<byte> GetMemory(int sizeHint = 0)
            {
                if ((_currentOwner.Memory.Length - _currentWriteOffset) >= sizeHint)
                {
                    return _currentOwner.Memory.Slice(_currentWriteOffset);
                }

                return GetMemorySlow(sizeHint);
            }

            private Memory<byte> GetMemorySlow(int sizeHint)
            {
                FlushCurrentBuffer(final: true);

                _currentOwner = _pool.Rent(Math.Max(sizeHint, MinimumSendBufferLength));
                return _currentOwner.Memory;
            }

            private void FlushCurrentBuffer(bool final)
            {
                if (_currentWriteOffset != _currentSendOffset)
                {
                    Memory<byte> sendBuffer = _currentOwner.Memory[_currentSendOffset.._currentWriteOffset];

                    SendOperation op = GetIdleSendOperation();
                    op.Task = op.Context.SendAsync(sendBuffer);

                    if (final || _currentWriteOffset == _currentOwner.Memory.Length)
                    {
                        op.MemoryOwner = _currentOwner;
                        _currentOwner = null;
                        _currentSendOffset = 0;
                        _currentWriteOffset = 0;
                    }

                    _totalSendBytesQueued += sendBuffer.Length;
                }
            }

            public override void Advance(int bytes)
            {
                int newWriteOffset = _currentWriteOffset + bytes;
                _currentWriteOffset = newWriteOffset;

                if (_currentWriteOffset == _currentOwner.Memory.Length)
                {
                    FlushCurrentBuffer(final: true);
                }
            }

            public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
            {
                FlushCurrentBuffer(final: false);

                if (_totalSendBytesQueued > _maxSendBytesQueued)
                {
                    do
                    {
                        SendOperation op = _sendOperations.Dequeue();
                        try
                        {
                            int sendBytes = await op.Task;
                            _totalSendBytesQueued -= sendBytes;
                        }
                        finally
                        {
                            ReturnSendOperation(op);
                        }
                    }
                    while (_totalSendBytesQueued > _resumeSendBytesQueued);
                }

                return new FlushResult(isCanceled: false, isCompleted: false);
            }

            public override void CancelPendingFlush()
            {
                throw new NotImplementedException();
            }

            public override void Complete(Exception exception = null)
            {
                throw new NotImplementedException($"Call {nameof(CompleteAsync)} instead.");
            }

            public override async ValueTask CompleteAsync(Exception exception = null)
            {
                if (exception == null)
                {
                    FlushCurrentBuffer(final: true);

                    while (_sendOperations.TryDequeue(out SendOperation op))
                    {
                        try
                        {
                            int sendBytes = await op.Task;
                            _totalSendBytesQueued -= sendBytes;
                        }
                        finally
                        {
                            ReturnSendOperation(op);
                        }
                    }
                }

                Shutdown(exception);
            }

            private SendOperation GetIdleSendOperation()
            {
                SendOperation op = _sendOperationCacheHead;

                if (op != null)
                {
                    op.Next = null;
                    return op;
                }

                op = new SendOperation(_socket.CreateOperationContext());
                return op;
            }

            private void ReturnSendOperation(SendOperation op)
            {
                if (op.MemoryOwner != null)
                {
                    op.MemoryOwner.Dispose();
                    op.MemoryOwner = null;
                }
                op.Task = default;

                op.Next = _sendOperationCacheHead;
                _sendOperationCacheHead = op;
            }

            private async Task ReceiveLoopAsync()
            {
                _receiveStart = new ReadOperation(_socket.CreateOperationContext(), _pool.Rent(_receiveSegmentLength));
                _receiveEnd = _receiveStart;
                _receiveStart.Task = _receiveStart.Context.ReceiveAsync(_receiveStart.MemoryOwner.Memory);

                while (true)
                {
                    int bytesReceived = await _receiveEnd.Task;

                    if (bytesReceived == 0)
                    {
                        break;
                    }

                    bool pause = false;

                    lock (_receiveSyncObj)
                    {
                        _receiveEndAvailable += bytesReceived;
                        _totalReceiveBytesAvailable += bytesReceived;

                        if (_receiveEndAvailable == _receiveEnd.Memory.Length)
                        {
                            pause = CompleteFullSegmentReceive();
                        }
                    }

                    if (pause)
                    {
                        await _pauseReceiveTaskSource.Task;

                        pause = CompleteFullSegmentReceive();
                        Debug.Assert(pause == false);
                    }

                    Debug.Assert(_receiveEndAvailable != _receiveEnd.Memory.Length);
                    _receiveEnd.Task = _receiveEnd.Context.ReceiveAsync(_receiveEnd.MemoryOwner.Memory.Slice(_receiveEndAvailable));
                }
            }

            /// <returns>If the reader should pause, true.</returns>
            private bool CompleteFullSegmentReceive()
            {
                // The current receive segment is full.

                if (_receiveEnd.Next != null)
                {
                    // There's a segment waiting already; start receiving to it.
                    _receiveEnd = (ReadOperation)_receiveEnd.Next;
                }
                else if (_totalReceiveBytesAvailable >= _maxReceiveBytesAvailable)
                {
                    // We've exceeded our stop value: wait for the pause task to resume reading.
                    _pauseReceiveTaskSource.Reset();
                    return true;
                }
                else
                {
                    ReadOperation next = new ReadOperation(_socket.CreateOperationContext(), _pool.Rent(_receiveSegmentLength));
                    _receiveEnd.SetNext(next);
                    _receiveEnd = next;
                    _receiveEndAvailable = 0;
                }

                return false;
            }

            public async ValueTask<ReadResult> ReaderReadAsync()
            {
                throw new NotImplementedException();
            }

            public bool ReaderTryRead(out ReadResult result)
            {
                throw new NotImplementedException();
            }

            private void Shutdown(Exception ex)
            {
            }
        }

        sealed class SendOperation
        {
            public readonly RegisteredOperationContext Context;

            public ValueTask<int> Task;
            public IMemoryOwner<byte> MemoryOwner;

            public SendOperation Next;

            public SendOperation(RegisteredOperationContext context)
            {
                Context = context;
            }
        }

        sealed class ReadOperation : ReadOnlySequenceSegment<byte>
        {
            public readonly RegisteredOperationContext Context;
            public readonly IMemoryOwner<byte> MemoryOwner;

            public ValueTask<int> Task;

            public ReadOperation(RegisteredOperationContext context, IMemoryOwner<byte> owner)
            {
                Context = context;
                MemoryOwner = owner;
                Memory = owner.Memory;
            }

            public void SetNext(ReadOperation op)
            {
                Next = op;
                op.RunningIndex = RunningIndex + Memory.Length;
            }
        }

        sealed class Reader : PipeReader
        {
            readonly State _state;

            public Reader(State state)
            {
                _state = state;
            }

            public override void AdvanceTo(SequencePosition consumed)
            {
                throw new NotImplementedException();
            }

            public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
            {
                throw new NotImplementedException();
            }

            public override void CancelPendingRead()
            {
                throw new NotImplementedException();
            }

            public override void Complete(Exception exception = null)
            {
                throw new NotImplementedException();
            }

            public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) => _state.ReaderReadAsync();

            public override bool TryRead(out ReadResult result)
            {
                throw new NotImplementedException();
            }
        }

        sealed class ResettableValueTaskSource : IValueTaskSource
        {
            private ManualResetValueTaskSourceCore<bool> _source;

            public ValueTask Task => new ValueTask(this, _source.Version);

            public void SetResult() => _source.SetResult(false);

            public void Reset() => _source.Reset();

            void IValueTaskSource.GetResult(short token)
                => _source.GetResult(token);

            ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
                => _source.GetStatus(token);

            void IValueTaskSource.OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
                => _source.OnCompleted(continuation, state, token, flags);
        }
    }
}
