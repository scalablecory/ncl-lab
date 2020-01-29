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
    public class RegisteredSocketConnection : ConnectionContext, IFeatureCollection, IConnectionIdFeature, IConnectionTransportFeature, IConnectionItemsFeature, IMemoryPoolFeature, IConnectionLifetimeFeature
    {
        private readonly Socket _socket;
        private readonly RegisteredSocket _registeredSocket;
        private readonly IDuplexPipe _application;
        private readonly Task _ioTask;
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private Exception _shutdownReason;

        public RegisteredSocketConnection(RegisteredMemoryPool memoryPool, Socket socket, RegisteredSocket registeredSocket)
        {
            _socket = socket;
            _registeredSocket = registeredSocket;
            ConnectionClosed = _cancellationTokenSource.Token;

            var pipeOptions = new PipeOptions(pool: memoryPool, useSynchronizationContext: false);
            var input = new Pipe(pipeOptions);
            var output = new Pipe(pipeOptions);
            _application = new DuplexPipe(output.Reader, input.Writer);
            Transport = new DuplexPipe(input.Reader, output.Writer);

            _ioTask = Task.WhenAll(DoSendAsync(), DoReceiveAsync());
        }

        private sealed class DuplexPipe : IDuplexPipe
        {
            public PipeReader Input { get; }
            public PipeWriter Output { get; }

            public DuplexPipe(PipeReader input, PipeWriter output)
            {
                Input = input;
                Output = output;
            }
        }

        public override async ValueTask DisposeAsync()
        {
            Transport.Input.Complete();
            Transport.Output.Complete();
            await _ioTask.ConfigureAwait(false);

            _registeredSocket.Dispose();
            _socket.Dispose();
            _cancellationTokenSource.Dispose();
        }

        private async Task DoSendAsync()
        {
            Exception shutdownReason = null;
            Exception unexpectedError = null;
            PipeReader applicationOutput = _application.Input;

            try
            {
                RegisteredOperationContext operationContext = new RegisteredOperationContext();
                ReadOnlyMemory<byte>[] sendBuffers = new ReadOnlyMemory<byte>[1];

                while (true)
                {
                    ReadResult readResult = await applicationOutput.ReadAsync().ConfigureAwait(false);

                    if (readResult.IsCanceled)
                    {
                        break;
                    }

                    int idx = 0;
                    foreach (ReadOnlyMemory<byte> segment in readResult.Buffer)
                    {
                        if (idx == sendBuffers.Length)
                        {
                            Array.Resize(ref sendBuffers, sendBuffers.Length * 2);
                        }

                        sendBuffers[idx] = segment;
                    }

                    int sentBytes = await _registeredSocket.SendAsync(sendBuffers.AsSpan(0, idx), operationContext).ConfigureAwait(false);

                    if (sentBytes == 0)
                    {
                        applicationOutput.Complete();
                        _application.Output.CancelPendingFlush();
                        break;
                    }

                    applicationOutput.AdvanceTo(readResult.Buffer.GetPosition(sentBytes));
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                shutdownReason = new ConnectionResetException(ex.Message, ex);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
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
            applicationOutput.Complete(unexpectedError);
            _application.Output.CancelPendingFlush();
        }

        private async Task DoReceiveAsync()
        {
            Exception error = null;
            PipeWriter applicationInput = _application.Output;

            try
            {
                RegisteredOperationContext operationContext = new RegisteredOperationContext();

                while (true)
                {
                    int bytesReceived = await _registeredSocket.ReceiveAsync(applicationInput.GetMemory(), operationContext).ConfigureAwait(false);

                    if (bytesReceived == 0)
                    {
                        break;
                    }

                    applicationInput.Advance(bytesReceived);

                    FlushResult flushResult = await applicationInput.FlushAsync();

                    if (flushResult.IsCompleted || flushResult.IsCanceled)
                    {
                        break;
                    }
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                error = new ConnectionResetException(ex.Message, ex);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            applicationInput.Complete(Volatile.Read(ref _shutdownReason) ?? error);
            _cancellationTokenSource.Cancel();
        }

        private void Shutdown(Exception shutdownReason)
        {
            if (_shutdownReason != null)
            {
                return;
            }

            shutdownReason ??= new ConnectionAbortedException("The Socket transport's send loop completed gracefully.");

            if (Interlocked.CompareExchange(ref _shutdownReason, shutdownReason, null) == null)
            {
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
        }

        public override void Abort(ConnectionAbortedException abortReason)
        {
            Shutdown(abortReason);
            _application.Input.CancelPendingRead();
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

        MemoryPool<byte> IMemoryPoolFeature.MemoryPool => MemoryPool<byte>.Shared;

        public override IDictionary<object, object> Items
        {
            get => _items ??= new Dictionary<object, object>();
            set => _items = value;
        }

        CancellationToken IConnectionLifetimeFeature.ConnectionClosed { get; set; }

        #endregion
    }
}
