using MobileDeliveryClient.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MobileDeliveryGeneral.Utilities;
using MobileDeliveryClient.Exceptions;
using MobileDeliveryClient.Threading;
using MobileDeliveryClient.MessageTypes;
using MobileDeliveryClient.Interfaces;

namespace MobileDeliveryClient
{
    /// <summary>
    /// A simple websocket client with built-in reconnection and error handling
    /// </summary>
    public partial class WebsocketClient : IWebsocketClient
    {
        private static readonly ILog Logger = GetLogger();

        private readonly WebsocketAsyncLock _locker = new WebsocketAsyncLock();
        private Uri _url;
        private Timer _lastChanceTimer;
        private readonly Func<Uri, CancellationToken, Task<WebSocket>> _connectionFactory;

        private DateTime _lastReceivedMsg = DateTime.UtcNow; 

        private bool _disposing;
        private bool _reconnecting;
        private bool _stopping;
        private bool _isReconnectionEnabled = true;
        private WebSocket _client;
        private CancellationTokenSource _cancellation;
        private CancellationTokenSource _cancellationTotal;

        public ObservableEvent<ResponseMessage> _messageReceivedSubject { get; private set; }
        public ObservableEvent<ReconnectionType> _reconnectionSubject { get; private set; }
        public ObservableEvent<DisconnectionType> _disconnectedSubject { get; private set; }

        //private readonly Subject<ResponseMessage> _messageReceivedSubject = new Subject<ResponseMessage>();
        //private readonly Subject<ReconnectionType> _reconnectionSubject = new Subject<ReconnectionType>();
        //private readonly Subject<DisconnectionType> _disconnectedSubject = new Subject<DisconnectionType>();

        /// <summary>
        /// A simple websocket client with built-in reconnection and error handling
        /// </summary>
        /// <param name="url">Target websocket url (wss://)</param>
        /// <param name="clientFactory">Optional factory for native ClientWebSocket, use it whenever you need some custom features (proxy, settings, etc)</param>
        public WebsocketClient(Uri url, Func<ClientWebSocket> clientFactory = null)
            : this(url, GetClientFactory(clientFactory))
        {
            _messageReceivedSubject = new ObservableEvent<ResponseMessage>();
            _reconnectionSubject = new ObservableEvent<ReconnectionType>();
            _disconnectedSubject = new ObservableEvent<DisconnectionType>();
        }

        /// <summary>
        /// A simple websocket client with built-in reconnection and error handling
        /// </summary>
        /// <param name="url">Target websocket url (wss://)</param>
        /// <param name="connectionFactory">Optional factory for native creating and connecting to a websocket. The method should return a <see cref="WebSocket"/> which is connected. Use it whenever you need some custom features (proxy, settings, etc)</param>
        public WebsocketClient(Uri url, Func<Uri, CancellationToken, Task<WebSocket>> connectionFactory)
        {
            Validations.Validations.ValidateInput(url, nameof(url));

            _url = url;
            _connectionFactory = connectionFactory ?? (async (uri, token) =>
            {
                var client = new ClientWebSocket2
                {
                    Options = { KeepAliveInterval = new TimeSpan(0, 0, 2, 0) ,  }
                };
                client.Options.RequestHeaders.Add("name","WebsocketClient");
               // client.Options.Ser\.Add(new System.Net.Cookie("ClientSocket"))
                await client.ConnectAsync(uri, token).ConfigureAwait(false);
                return client;
            }); 
        }

        /// <inheritdoc />
        public Uri Url
        {
            get => _url;
            set
            {
                Validations.Validations.ValidateInput(value, nameof(Url));
                _url = value;
            }
        }

        /// <summary>
        /// Stream with received message (raw format)
        /// </summary>
        public IObservable2<ResponseMessage> MessageReceived => _messageReceivedSubject;



        /// <summary>
        /// Stream for reconnection event (triggered after the new connection) 
        /// </summary>
        public IObservable2<ReconnectionType> ReconnectionHappened => _reconnectionSubject;

        /// <summary>
        /// Stream for disconnection event (triggered after the connection was lost) 
        /// </summary>
        public IObservable2<DisconnectionType> DisconnectionHappened => _disconnectedSubject;

        /// <summary>
        /// Time range in ms, how long to wait before reconnecting if no message comes from server.
        /// Default 60000 ms (1 minute)
        /// </summary>
        public int ReconnectTimeoutMs { get; set; } = 60 * 1000;

        /// <summary>
        /// Time range in ms, how long to wait before reconnecting if last reconnection failed.
        /// Default 60000 ms (1 minute)
        /// </summary>
        public int ErrorReconnectTimeoutMs { get; set; } = 60 * 1000;

        /// <summary>
        /// Enable or disable reconnection functionality (enabled by default)
        /// </summary>
        public bool IsReconnectionEnabled
        {
            get => _isReconnectionEnabled;
            set
            {
                _isReconnectionEnabled = value;

                if (IsStarted)
                {
                    if (_isReconnectionEnabled)
                    {
                        ActivateLastChance();
                    }
                    else
                    {
                        DeactivateLastChance();
                    }
                }
            }
        }

        /// <summary>
        /// Get or set the name of the current websocket client instance.
        /// For logging purpose (in case you use more parallel websocket clients and want to distinguish between them)
        /// </summary>
        public string Name { get; set;}

        /// <summary>
        /// Returns true if Start() method was called at least once. False if not started or disposed
        /// </summary>
        public bool IsStarted { get; private set; }

        /// <summary>
        /// Returns true if client is running and connected to the server
        /// </summary>
        public bool IsRunning { get; private set; }

        public bool sIsRunning { get; private set; }


        /// <inheritdoc />
        public Encoding MessageEncoding { get; set; }

        /// <inheritdoc />
        public ClientWebSocket2 NativeClient => GetSpecificOrThrow(_client);

        IObservable2<ResponseMessage> IWebsocketClient.MessageReceived => _messageReceivedSubject;

        IObservable2<ReconnectionType> IWebsocketClient.ReconnectionHappened => _reconnectionSubject;

        IObservable2<DisconnectionType> IWebsocketClient.DisconnectionHappened => _disconnectedSubject;

        /// <summary>
        /// Terminate the websocket connection and cleanup everything
        /// </summary>
        public void Dispose()
        {
            _disposing = true;
            Logger.Debug(L("Disposing.."));
            try
            {
                _lastChanceTimer?.Dispose();
                _cancellation?.Cancel();
                _cancellationTotal?.Cancel();
                _client?.Abort();
                _client?.Dispose();
                _cancellation?.Dispose();
                _cancellationTotal?.Dispose();
                _messagesTextToSendQueue?.Dispose();
                _messagesBinaryToSendQueue?.Dispose();
            }
            catch (Exception e)
            {
                Logger.Error(e, L($"Failed to dispose client, error: {e.Message}"));
            }

            IsRunning = false;
            sIsRunning = false;
            IsStarted = false;
            //_disconnectedSubject.OnNext(DisconnectionType.Exit);
            _disconnectedSubject.Raise(DisconnectionType.Exit);
        }
       
        /// <summary>
        /// Start listening to the websocket stream on the background thread
        /// </summary>
        public async Task Start()
        {
            if (IsStarted)
            {
                Logger.Debug(L("Client already started, ignoring.."));
                return;
            }
            IsStarted = true;

            Logger.Debug(L("Starting.."));
            _cancellation = new CancellationTokenSource();
            _cancellationTotal = new CancellationTokenSource();

            await StartClient(_url, _cancellation.Token, ReconnectionType.Initial).ConfigureAwait(false);

            StartBackgroundThreadForSendingText();
            StartBackgroundThreadForSendingBinary();
            IsRunning = true;
            sIsRunning = true;
        }

        /// <inheritdoc />
        public async Task<bool> Stop(WebSocketCloseStatus status, string statusDescription)
        {
            if (_client == null)
            {
                IsStarted = false;
                IsRunning = false;
                sIsRunning = false;
                return false;
            }

            try
            {
                _stopping = true;
                await _client.CloseAsync(status, statusDescription, _cancellation?.Token ?? CancellationToken.None);
            }
            catch (Exception e)
            {
                Logger.Error(L($"Error while stopping client, message: '{e.Message}'"));
            }
            
            DeactivateLastChance();
            IsStarted = false;
            IsRunning = false;
            sIsRunning = false;
            _stopping = false;
            return true;
        }

        private static Func<Uri, CancellationToken, Task<WebSocket>> GetClientFactory(Func<ClientWebSocket> clientFactory)
        {
            if (clientFactory == null)
                return null;

            return (async (uri, token) => {
                var client = clientFactory();
                await client.ConnectAsync(uri, token).ConfigureAwait(false);
                return client;
            });
        }

        static object bstarting = new object();
        private async Task StartClient(Uri uri, CancellationToken token, ReconnectionType type)
        {
            //try
            //{
            //if (!IsRunning)
            //if (Monitor.TryEnter(bstarting, 100))
            if (!sIsRunning)
            {
                IsRunning = true;
                sIsRunning = true;

                DeactivateLastChance();

                try
                {
                    _client = await _connectionFactory(uri, token).ConfigureAwait(false);

                    //_reconnectionSubject.OnNext(type);
                    _reconnectionSubject.Raise(type);

#pragma warning disable 4014
                    Listen(_client, token);
#pragma warning restore 4014
                    _lastReceivedMsg = DateTime.UtcNow;
                    ActivateLastChance();
                }
                catch (Exception e)
                {
                    //_disconnectedSubject.OnNext(DisconnectionType.Error);
                    _disconnectedSubject.Raise(DisconnectionType.Error);
                    Logger.Error(e, L($"Exception while connecting. " +
                                      $"Waiting {ErrorReconnectTimeoutMs / 1000} sec before next reconnection try."));
                    await Task.Delay(ErrorReconnectTimeoutMs, token).ConfigureAwait(false);
                    await Reconnect(ReconnectionType.Error).ConfigureAwait(false);
                }
                // }
                //  else
                Logger.Error("StartClient: Client already running.  Monitor Locked!");
                // }
                //catch (Exception ex)
                //{ Logger.Error("StartClient Exception: " + ex.Message); }
                //finally
                //{
                //   // if (blockTaken)
                //        Monitor.Exit(bstarting);
                //}
                IsRunning = false;
                sIsRunning = false;
            }
        }

        private bool IsClientConnected()
        {
            try
            {
                return _client.State == WebSocketState.Open;
            }
            catch (Exception e) { Logger.Error("Client connection is null!  Exception: " + e.Message ); }
            return false;
        }

        private async Task Listen(WebSocket client, CancellationToken token)
        {
            try
            {
                do
                {
                    var buffer = new ArraySegment<byte>(new byte[8192]);

                    using (var ms = new MemoryStream())
                    {
                        WebSocketReceiveResult result=null;
                        do
                        {
                            try
                            {
                                result = await client.ReceiveAsync(buffer, token);
                                if(buffer.Array != null)
                                    ms.Write(buffer.Array, buffer.Offset, result.Count);
                            }
                            catch (Exception ex) {
                                sIsRunning = false;
                                Logger.Error("WebSocketClient Exception: " + ex.Message); }
                        } while (!result.EndOfMessage);

                        ms.Seek(0, SeekOrigin.Begin);

                        ResponseMessage message;
                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            var data = GetEncoding().GetString(ms.ToArray());
                            message = ResponseMessage.TextMessage(data);
                        }
                        else
                        {
                            var data = ms.ToArray();
                            message = ResponseMessage.BinaryMessage(data);
                        }

                        Logger.Trace(L($"Received:  {message}"));
                        _lastReceivedMsg = DateTime.UtcNow;
                        //_messageReceivedSubject.OnNext(message);
                        _messageReceivedSubject.Raise(message);
                    }

                } while (client.State == WebSocketState.Open && !token.IsCancellationRequested);
            }
            catch (TaskCanceledException) // e)
            {
               // Logger.Error(e, L($"Task was canceled, ignore. Exception: '{e.Message}'"));
            }
            catch (OperationCanceledException )//e)
            {
             //   Logger.Error(e, L($"Operation was canceled, ignore. Exception: '{e.Message}'"));
            }
            catch (ObjectDisposedException)// e)
            {
              //  Logger.Error(e, L($"Client was disposed, ignore. Exception: '{e.Message}'"));
            }
            catch (Exception e)
            {
               // Logger.Error(e, L($"Error while listening to websocket stream, error: '{e.Message}'"));
            }


            if (_disposing || _reconnecting || _stopping || !IsStarted)
                return;

            // listening thread is lost, we have to reconnect
#pragma warning disable 4014
            ReconnectSynchronized(ReconnectionType.Lost);
#pragma warning restore 4014
        }

        private Encoding GetEncoding()
        {
            if (MessageEncoding == null)
                MessageEncoding = Encoding.UTF8;
            return MessageEncoding;
        }

        private ClientWebSocket2 GetSpecificOrThrow(WebSocket client)
        {
            if (client == null)
                return null;
            var specific = client as ClientWebSocket2;
            if(specific == null)
                throw new WebsocketException("Cannot cast 'WebSocket' client to 'ClientWebSocket', " +
                                             "provide correct type via factory or don't use this property at all.");
            return specific;
        }

        private string L(string msg)
        {
            var name = Name ?? "CLIENT";
            return $"[WEBSOCKET {name}] {msg}";
        }

        private static ILog GetLogger()
        {
            try
            {
                return LogProvider.GetCurrentClassLogger();
            }
            catch (Exception e)
            {
                Trace.WriteLine($"[WEBSOCKET] Failed to initialize logger, disabling.. " +
                                $"Error: {e}");
                return LogProvider.NoOpLogger.Instance;
            }
        }

        private DisconnectionType TranslateTypeToDisconnection(ReconnectionType type)
        {
            // beware enum indexes must correspond to each other
            return (DisconnectionType) type;
        }
    }
}
