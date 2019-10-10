﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;

namespace Fleck
{
    public class WebSocketConnection : IWebSocketConnection
    {
        public WebSocketConnection(
            ISocket socket,
            Action<IWebSocketConnection> initialize,
            Func<byte[], WebSocketHttpRequest> parseRequest,
            Func<WebSocketHttpRequest, IHandler> handlerFactory)
        {
            Socket = socket;
            OnOpen = () => { };
            OnClose = () => { };
            OnMessage = x => { };
            OnBinary = x => { };
            OnPing = x => { };
            OnPong = x => { };
            OnError = x => { };
            _initialize = initialize;
            _handlerFactory = handlerFactory;
            _parseRequest = parseRequest;
        }

        public ISocket Socket { get; set; }

        private readonly Action<IWebSocketConnection> _initialize;
        private readonly Func<WebSocketHttpRequest, IHandler> _handlerFactory;
        readonly Func<byte[], WebSocketHttpRequest> _parseRequest;

        public IHandler Handler { get; set; }

        private bool _closing;
        private bool _closed;
        private const int ReadSize = 1024 * 4;

        public Action OnOpen { get; set; }

        public Action OnClose { get; set; }

        public Action<string> OnMessage { get; set; }

        public BinaryDataHandler OnBinary { get; set; }

        public BinaryDataHandler OnPing { get; set; }

        public BinaryDataHandler OnPong { get; set; }

        public Action<Exception> OnError { get; set; }

        public IWebSocketConnectionInfo ConnectionInfo { get; private set; }

        public bool IsAvailable => !_closing && !_closed && Socket.Connected;

        public Task Send(string message)
        {
            return Send(Handler.FrameText(message));
        }

        public Task Send(byte[] message)
        {
            return Send(Handler.FrameBinary(message));
        }

        public Task SendPing(byte[] message)
        {
            return Send(Handler.FramePing(message));
        }

        public Task SendPong(byte[] message)
        {
            return Send(Handler.FramePong(message));
        }

        private Task Send(MemoryBuffer buffer)
        {
            if (Handler == null)
                throw new InvalidOperationException("Cannot send before handshake");

            if (!IsAvailable)
            {
                const string errorMessage = "Data sent while closing or after close. Ignoring.";
                FleckLog.Warn(errorMessage);

                var taskForException = new TaskCompletionSource<object>();
                taskForException.SetException(new ConnectionNotAvailableException(errorMessage));
                return taskForException.Task;
            }

            return SendBytes(buffer);
        }

        public void StartReceiving()
        {
            var data = new List<byte>(ReadSize);
            var buffer = new byte[ReadSize];
            Read(data, buffer);
        }

        public void Close()
        {
            Close(WebSocketStatusCodes.NormalClosure);
        }

        public void Close(ushort code)
        {
            if (!IsAvailable)
                return;

            _closing = true;

            if (Handler == null)
            {
                CloseSocket();
                return;
            }

            var bytes = Handler.FrameClose(code);
            if (bytes.Length == 0)
                CloseSocket();
            else
                SendBytes(bytes, CloseSocket);
        }

        public void CreateHandler(IEnumerable<byte> data)
        {
            var request = _parseRequest(data.ToArray());
            if (request == null)
                return;
            Handler = _handlerFactory(request);
            if (Handler == null)
                return;
            ConnectionInfo = WebSocketConnectionInfo.Create(request, Socket.RemoteIpAddress, Socket.RemotePort);

            _initialize(this);

            var handshake = Handler.CreateHandshake();
            SendBytes(handshake, OnOpen);
        }

        private void Read(List<byte> data, byte[] buffer)
        {
            if (!IsAvailable)
                return;

            Socket.Receive(buffer, r =>
            {
                if (r <= 0)
                {
                    FleckLog.Debug("0 bytes read. Closing.");
                    CloseSocket();
                    return;
                }
                FleckLog.Debug(r + " bytes read");
                var readBytes = new Span<byte>(buffer, 0, r);
                if (Handler != null)
                {
                    Handler.Receive(readBytes);
                }
                else
                {
                    for (var i = 0; i < readBytes.Length; i++)
                        data.Add(readBytes[i]);

                    CreateHandler(data);
                }

                Read(data, buffer);
            },
            HandleReadError);
        }

        private void HandleReadError(Exception e)
        {
            if (e is AggregateException)
            {
                var agg = e as AggregateException;
                HandleReadError(agg.InnerException);
                return;
            }

            if (e is ObjectDisposedException)
            {
                FleckLog.Debug("Swallowing ObjectDisposedException", e);
                return;
            }

            OnError(e);

            if (e is WebSocketException)
            {
                FleckLog.Debug("Error while reading", e);
                Close(((WebSocketException)e).StatusCode);
            }
            else if (e is IOException)
            {
                FleckLog.Debug("Error while reading", e);
                Close(WebSocketStatusCodes.AbnormalClosure);
            }
            else
            {
                FleckLog.Error("Application Error", e);
                Close(WebSocketStatusCodes.InternalServerError);
            }
        }

        private Task SendBytes(MemoryBuffer bytes, Action callback = null)
        {
            return Socket.Send(bytes, () =>
            {
                FleckLog.Debug("Sent " + bytes.Length + " bytes");
                bytes.Dispose();
                callback?.Invoke();
            }, e =>
            {
                if (e is IOException)
                    FleckLog.Debug("Failed to send. Disconnecting.", e);
                else
                    FleckLog.Info("Failed to send. Disconnecting.", e);

                bytes.Dispose();
                CloseSocket();
            });
        }

        private void CloseSocket()
        {
            _closing = true;
            OnClose();
            _closed = true;
            Socket.Close();
            Socket.Dispose();
            _closing = false;
        }
    }
}
