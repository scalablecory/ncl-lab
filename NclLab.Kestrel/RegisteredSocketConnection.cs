using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using NclLab.Sockets;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NclLab.Kestrel
{
    internal class RegisteredSocketConnection : ConnectionContext, IFeatureCollection, IConnectionIdFeature, IConnectionTransportFeature, IConnectionItemsFeature, IMemoryPoolFeature, IConnectionLifetimeFeature, IDuplexPipe
    {
        private readonly RegisteredSocket _socket;

        private readonly PipeReader _transportInput, _socketInput;
        private readonly PipeWriter _transportOutput, _socketOutput;

        private readonly Task _ioTask;
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private Exception _shutdownReason;

        public RegisteredSocketConnection(RegisteredMemoryPool memoryPool, RegisteredSocket socket, long? maxReadBufferSize, long? maxWriteBufferSize)
        {
            _features.Add(typeof(IConnectionIdFeature), this);
            _features.Add(typeof(IConnectionTransportFeature), this);
            _features.Add(typeof(IConnectionItemsFeature), this);
            _features.Add(typeof(IMemoryPoolFeature), this);
            _features.Add(typeof(IConnectionLifetimeFeature), this);

            _socket = socket;
            ConnectionClosed = _cancellationTokenSource.Token;
            LocalEndPoint = socket.LocalEndPoint;
            RemoteEndPoint = socket.RemoteEndPoint;

            var inputPipeOptions = new PipeOptions(pool: memoryPool, pauseWriterThreshold: maxReadBufferSize.GetValueOrDefault(), resumeWriterThreshold: maxReadBufferSize.GetValueOrDefault() / 2, useSynchronizationContext: false);
            var inputPipe = new Pipe(inputPipeOptions);

            var outputPipeOptions = new PipeOptions(pool: memoryPool, pauseWriterThreshold: maxWriteBufferSize.GetValueOrDefault(), resumeWriterThreshold: maxWriteBufferSize.GetValueOrDefault() / 2, useSynchronizationContext: false);
            var outputPipe = new Pipe(outputPipeOptions);

            (_transportInput, _transportOutput) = (inputPipe.Reader, outputPipe.Writer);
            (_socketInput, _socketOutput) = (outputPipe.Reader, inputPipe.Writer);
            Transport = this;

            _ioTask = Task.WhenAll(DoReceiveAsync(), DoSendAsync());
        }

        public override async ValueTask DisposeAsync()
        {
            Transport.Input.Complete();
            Transport.Output.Complete();
            await _ioTask;

            _socket.Dispose();
            _cancellationTokenSource.Dispose();
        }

        private async Task DoSendAsync()
        {
            Exception shutdownReason = null;
            Exception unexpectedError = null;

            try
            {
                using RegisteredOperationContext operationContext = _socket.CreateOperationContext();
                var sendBuffers = new ReadOnlyMemory<byte>[1];

                while (true)
                {
                    ReadResult readResult = await _socketInput.ReadAsync();

                    if (readResult.IsCanceled)
                    {
                        break;
                    }

                    ReadOnlySequence<byte> buffer = readResult.Buffer;

                    if (!buffer.IsEmpty)
                    {
                        ValueTask<int> bytesSentTask;

                        if (buffer.IsSingleSegment)
                        {
                            bytesSentTask = operationContext.SendAsync(buffer.First);
                        }
                        else
                        {
                            int idx = 0;
                            foreach (ReadOnlyMemory<byte> segment in readResult.Buffer)
                            {
                                if (idx == sendBuffers.Length)
                                {
                                    Array.Resize(ref sendBuffers, sendBuffers.Length * 2);
                                }

                                sendBuffers[idx++] = segment;
                            }

                            bytesSentTask = operationContext.SendAsync(sendBuffers.AsSpan(0, idx));
                        }

                        int bytesSent = await bytesSentTask;
                        _socketInput.AdvanceTo(buffer.GetPosition(bytesSent));
                    }
                    else if (readResult.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (SocketException ex) when (IsConnectionResetError(ex.SocketErrorCode))
            {
                shutdownReason = new ConnectionResetException(ex.Message, ex);
            }
            catch (SocketException ex) when (IsConnectionAbortError(ex.SocketErrorCode))
            {
                shutdownReason = ex;
            }
            catch (ObjectDisposedException ex)
            {
                shutdownReason = ex;
            }
            catch (Exception ex)
            {
                shutdownReason = ex;
                unexpectedError = ex;
            }

            Shutdown(shutdownReason);
            _socketInput.Complete(unexpectedError);
            _socketOutput.CancelPendingFlush();
        }

        private async Task DoReceiveAsync()
        {
            Exception error = null;

            try
            {
                using RegisteredOperationContext operationContext = _socket.CreateOperationContext();

                while (true)
                {
                    Memory<byte> memory = _socketOutput.GetMemory();
                    int bytesReceived = await operationContext.ReceiveAsync(memory);

                    if (bytesReceived == 0)
                    {
                        break;
                    }

                    _socketOutput.Advance(bytesReceived);

                    FlushResult flushResult = await _socketOutput.FlushAsync();

                    if (flushResult.IsCompleted || flushResult.IsCanceled)
                    {
                        break;
                    }
                }
            }
            catch (SocketException ex) when (IsConnectionResetError(ex.SocketErrorCode))
            {
                error = new ConnectionResetException(ex.Message, ex);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            _socketOutput.Complete(Volatile.Read(ref _shutdownReason) ?? error);
            _cancellationTokenSource.Cancel();
        }

        private static bool IsConnectionResetError(SocketError errorCode)
        {
            return errorCode == SocketError.ConnectionReset ||
                   errorCode == SocketError.Shutdown ||
                   errorCode == SocketError.ConnectionAborted;
        }

        private static bool IsConnectionAbortError(SocketError errorCode)
        {
            return errorCode == SocketError.OperationAborted ||
                   errorCode == SocketError.Interrupted;
        }

        private void Shutdown(Exception shutdownReason)
        {
            if (_shutdownReason != null)
            {
                // Already shut down.
                return;
            }

            shutdownReason ??= new ConnectionAbortedException("The Socket transport's send loop completed gracefully.");

            if (Interlocked.CompareExchange(ref _shutdownReason, shutdownReason, null) != null)
            {
                // Already shut down, another thread won a race.
                return;
            }

            // Actually shut down.

            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // ignored.
            }

            _socket.Dispose();
        }

        public override void Abort(ConnectionAbortedException abortReason)
        {
            Shutdown(abortReason);
            _socketInput.CancelPendingRead();
        }

        #region Boilerplate nonsense

        private readonly Dictionary<Type, object> _features = new Dictionary<Type, object>();
        private int _featureRevision = 0;

        private string _connectionId;
        private static long s_nextConnectionId;

        private IDictionary<object, object> _items;

        public override IFeatureCollection Features => this;

        object IFeatureCollection.this[Type key]
        {
            get
            {
                _features.TryGetValue(key, out object value);
                return value;
            }
            set
            {
                _features[key] = value;
                ++_featureRevision;
            }
        }

        bool IFeatureCollection.IsReadOnly => false;

        int IFeatureCollection.Revision => _featureRevision;

        TFeature IFeatureCollection.Get<TFeature>()
        {
            _features.TryGetValue(typeof(TFeature), out object value);
            return (TFeature)value;
        }

        void IFeatureCollection.Set<TFeature>(TFeature instance)
        {
            _features[typeof(TFeature)] = instance;
            ++_featureRevision;
        }

        public override string ConnectionId
        {
            get => _connectionId ??= Interlocked.Increment(ref s_nextConnectionId).ToString(CultureInfo.InvariantCulture);
            set => _connectionId = value;
        }

        IEnumerator<KeyValuePair<Type, object>> IEnumerable<KeyValuePair<Type, object>>.GetEnumerator()
        {
            return _features.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _features.GetEnumerator();
        }

        public override IDuplexPipe Transport { get; set; }

        PipeReader IDuplexPipe.Input => _transportInput;
        PipeWriter IDuplexPipe.Output => _transportOutput;

        MemoryPool<byte> IMemoryPoolFeature.MemoryPool => MemoryPool<byte>.Shared;

        public override IDictionary<object, object> Items
        {
            get => _items ??= new Dictionary<object, object>();
            set => _items = value;
        }

        #endregion
    }
}
