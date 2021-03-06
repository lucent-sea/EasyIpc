using MessagePack;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EasyIpc
{
    public interface IConnectionBase : IDisposable
    {
        event EventHandler<IConnectionBase> ReadingEnded;

        bool IsConnected { get; }
        string PipeName { get; }


        void BeginRead(CancellationToken cancellationToken);
        Stream GetStream();
        Task<Result<TReturnType>> Invoke<TContentType, TReturnType>(TContentType content, int timeoutMs = 5000);

        void Off<TContentType>();
        void Off<TContentType>(CallbackToken callbackToken);
        CallbackToken On<TContentType>(Action<TContentType> callback);

        CallbackToken On<TContentType, ReturnType>(Func<TContentType, ReturnType> handler);
        Task Send<TContentType>(TContentType content, int timeoutMs = 5000);
    }


    internal abstract class ConnectionBase : IConnectionBase
    {
        protected readonly SemaphoreSlim _initLock = new(1, 1);
        protected readonly ILogger _logger;
        protected PipeStream _pipeStream;

        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<MessageWrapper>> _invokesPendingCompletion = new();
        private readonly ICallbackStore _callbackStore;
        private CancellationToken _readStreamCancelToken;
        private Task _readTask;


        public ConnectionBase(ICallbackStoreFactory callbackFactory, ILogger logger)
        {
            _callbackStore = callbackFactory?.Create() ?? throw new ArgumentNullException(nameof(callbackFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public event EventHandler<IConnectionBase> ReadingEnded;

        public bool IsConnected => _pipeStream?.IsConnected ?? false;
        public string PipeName { get; protected set; }
        public void BeginRead(CancellationToken cancellationToken)
        {
            if (_readTask?.IsCompleted == false)
            {
                throw new InvalidOperationException("Stream is already being read.");
            }

            _readStreamCancelToken = cancellationToken;
            _readTask = Task.Run(ReadFromStream, cancellationToken);
        }

        public void Dispose()
        {
            _pipeStream?.Dispose();
        }

        public Stream GetStream()
        {
            return _pipeStream;
        }

        public async Task<Result<TReturnType>> Invoke<TReturnType>(MessageWrapper wrapper, int timeoutMs = 5000)
        {
            try
            {
                var tcs = new TaskCompletionSource<MessageWrapper>();
                if (!_invokesPendingCompletion.TryAdd(wrapper.Id, tcs))
                {
                    _logger.LogWarning("Already waiting for invoke completion of message ID {id}.", wrapper.Id);
                    return Result.Fail<TReturnType>($"Already waiting for invoke completion of message ID {wrapper.Id}.");
                }

                await SendInternal(wrapper, timeoutMs);

                if (!await Task.Run(() => tcs.Task.Wait(timeoutMs)))
                {
                    _logger.LogWarning("Timed out while invoking message type {contentType}.", wrapper.ContentType);

                    return Result.Fail<TReturnType>("Timed out while invoking message.");
                }

                var result = tcs.Task.Result;

                return Result.Ok((TReturnType)MessagePackSerializer.Deserialize(result.ContentType, result.Content));
            }
            finally
            {
                _invokesPendingCompletion.TryRemove(wrapper.Id, out _);
            }
        }

        public Task<Result<TReturnType>> Invoke<TContentType, TReturnType>(TContentType content, int timeoutMs = 5000)
        {
            var wrapper = new MessageWrapper(typeof(TContentType), content, MessageType.Invoke);

            return Invoke<TReturnType>(wrapper, timeoutMs);
        }

        public void Off<TContentType>()
        {
            if (!_callbackStore.TryRemoveAll(typeof(TContentType)))
            {
                _logger.LogWarning("The message type {contentType} wasn't found in the callback colection.", typeof(TContentType));
            }
        }

        public void Off<TContentType>(CallbackToken callbackToken)
        {
            if (!_callbackStore.TryRemove(typeof(TContentType), callbackToken))
            {
                _logger.LogWarning("The message type {contentType} wasn't found in the callback colection.", typeof(TContentType));
            }
        }

        public CallbackToken On<TContentType>(Action<TContentType> callback)
        {
            if (callback is null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            var objectCallback = new Action<object>(x => callback((TContentType)x));

            return _callbackStore.Add(typeof(TContentType), objectCallback);
        }


        public CallbackToken On<TContentType, ReturnType>(Func<TContentType, ReturnType> handler)
        {
            if (handler is null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            var objectHandler = new Func<object, object>(x => handler((TContentType)x));

            return _callbackStore.Add(objectHandler, typeof(TContentType), typeof(ReturnType));
        }

        public Task Send<TContentType>(TContentType content, int timeoutMs = 5000)
        {
            return SendInternal(typeof(TContentType), content, timeoutMs);
        }

        private void OnReadingEnded()
        {
            ReadingEnded?.Invoke(this, this);
        }

        private async Task ProcessMessage(MessageWrapper wrapper)
        {
            switch (wrapper.MessageType)
            {
                case MessageType.Response:
                    {
                        if (_invokesPendingCompletion.TryGetValue(wrapper.ResponseTo, out var tcs))
                        {
                            tcs.SetResult(wrapper);
                        }
                        break;
                    }
                case MessageType.Send:
                    {
                        await _callbackStore.InvokeActions(wrapper);
                        break;
                    }
                case MessageType.Invoke:
                    {
                        await _callbackStore.InvokeFuncs(wrapper, async result =>
                        {
                            await SendInternal(result);
                        });
                        break;
                    }
                case MessageType.Unspecified:
                default:
                    _logger.LogWarning("Unexpected message type: {messageType}", wrapper.MessageType);
                    break;
            }
        }

        private async Task ReadFromStream()
        {
            while (_pipeStream.IsConnected)
            {
                try
                {
                    if (_readStreamCancelToken.IsCancellationRequested)
                    {
                        _logger.LogDebug("IPC connection read cancellation requested.  Pipe Name: {pipeName}", PipeName);
                        break;
                    }

                    var messageSizeBuffer = new byte[4];
                    await _pipeStream.ReadAsync(messageSizeBuffer, 0, 4, _readStreamCancelToken);
                    var messageSize = BitConverter.ToInt32(messageSizeBuffer, 0);

                    var buffer = new byte[messageSize];

                    var bytesRead = 0;

                    while (bytesRead < messageSize)
                    {
                        bytesRead += await _pipeStream.ReadAsync(buffer, 0, messageSize, _readStreamCancelToken);
                    }

                    var wrapper = MessagePackSerializer.Deserialize<MessageWrapper>(buffer);

                    await ProcessMessage(wrapper);
                }
                catch (ThreadAbortException ex)
                {
                    _logger.LogInformation(ex, "IPC connection aborted.  Pipe Name: {pipeName}", PipeName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to process pipe message.", ex);
                }
            }

            _logger.LogDebug("IPC stream reading ended. Pipe Name: {pipeName}", PipeName);
            OnReadingEnded();
        }
        private Task SendInternal(Type contentType, object content, int timeoutMs = 5000)
        {
            var wrapper = new MessageWrapper(contentType, content, MessageType.Send);
            return SendInternal(wrapper, timeoutMs);
        }
        private async Task SendInternal(MessageWrapper wrapper, int timeoutMs = 5000)
        {
            try
            {
                if (timeoutMs < 1)
                {
                    throw new ArgumentException("Timeout must be greater than 0.");
                }

                using var cts = new CancellationTokenSource(timeoutMs);
                var wrapperBytes = MessagePackSerializer.Serialize(wrapper);

                var messageSizeBuffer = BitConverter.GetBytes(wrapperBytes.Length);
                await _pipeStream.WriteAsync(messageSizeBuffer, 0, messageSizeBuffer.Length, cts.Token);

                await _pipeStream.WriteAsync(wrapperBytes, 0, wrapperBytes.Length, cts.Token);
                await _pipeStream.FlushAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error sending message.  Content Type: {contentType}", wrapper.ContentType);
            }
        }
    }
}
