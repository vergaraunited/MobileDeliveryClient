using MobileDeliveryLogger;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using MobileDeliveryGeneral.Utilities;
using MobileDeliveryGeneral.Interfaces;
using static MobileDeliveryGeneral.Definitions.MsgTypes;
using MobileDeliveryClient.Interfaces;
using MobileDeliveryClient.MessageTypes;
using MobileDeliveryGeneral.Settings;

namespace MobileDeliveryClient.API
{
    public class ClientToServerConnection
    {
        private static readonly ManualResetEvent ExitEvent = new ManualResetEvent(false);
        
        public string url { get; set; }
        ushort port { get; set; }
        string name { get; set;}


        SocketSettings socSet;

        ReceiveMsgDelegate rm;
        isaSendMessageCallback sm;
        IWebsocketClient oclient;
        Timer timer;

        private bool bRunning { get; set; }
        private bool bStopped { get; set; }
        
        public ClientToServerConnection(SocketSettings socSet, ref SendMsgDelegate sm, ReceiveMsgDelegate rm)
        {
            Logger.Info($"{name} Client Connectng to server Connection: ws://{url}:{port}");
            this.url = socSet.url;
            this.port = socSet.port;
            this.name = socSet.name;
            this.socSet = socSet;
            this.rm = rm;
            sm = new SendMsgDelegate(SendCommandToServer);
            Logger.AppName = socSet.name;
        }

        public ClientToServerConnection(string url, ushort port, string name)
        {
            this.url = url;
            this.port = port;
            this.name = name;
            Logger.AppName = name;
        }

        void RecvdMsgEvent(isaCommand rcvMsg)
        {
            rm(rcvMsg);
            Logger.Debug($"Client Received Message From Server Event {name}");
        }

        public bool SendCommandToServer(isaCommand inputparam)
        {
            try
            {
                Logger.Debug($"SendWinsysCommand {name}");
                SendMsgEventAsync(inputparam);
                return true;
            }
            catch (Exception ex) { }
            return false;
        }
        //public bool CompleteStop(isaCommand inputparam)
        //{
        //    //var output = mp.CompleteStop(inputparam);
        //    return true;
        //}


        void SendMsgEventAsync(isaCommand cmd)
        {
            if (oclient == null)
            {
                Logger.Info($"UMDServerConnection:Task SendMsgEventAsync: Client {name} not connected");
                if (!bStopped)
                {
                    bRunning = false;
                    //StartAsync();
                    Thread.Sleep(100);
                }
            }
            Logger.Info($"UMDServerConnection SendMsgEventAsync {name} {cmd.ToString()}");
            Task retTsk = oclient.Send(cmd.ToArray());
            if (timer!=null)
                timer.Change(200, 10000);
            if (retTsk.Status == TaskStatus.Faulted)
                Logger.Info($"UMDServerConnection:Task SendMsgEventAsync: faulted {name}  " + cmd.ToString());
        }

        Task connectionTask;
        public async void StartAsync()
        {
            bool locked = false;
            try
            {
                Monitor.TryEnter(lconn, 500, ref locked);
                if (locked)
                {
                    if (!bRunning)
                    {
                        WaitHandle connectedwh = new AutoResetEvent(initialState: false);
                        
                        connectionTask = new Task(async () => await ConnectAsync(rm, socSet, connectedwh));
                        connectionTask.Start();

                        bool signaled = connectedwh.WaitOne(5000);
                        if (signaled)
                        {
                            Logger.Info("Connected");
                            //timer = new Timer(SendPing, this, 5000, 10000);
                        }
                        else
                        {
                            Logger.Info("Connection Timedout");
                        }

                        return;
                    }
                    Logger.Error("Already Running!  Disconnect first.");
                }
                else
                    throw new Exception("Deadlock StartAsync a client connection. " + name + "////:" + url + ":" + port );
            }
            catch (Exception e) { }
            finally { if (locked) Monitor.Exit(lconn); }
               
        }

        public void Disconnect()
        {
            Logger.Info("Disconnecting {name}.");
            bRunning = false;
            int cnt = 0;
            while (!bStopped && cnt++<=10)
                Thread.Sleep(100);
            ExitEvent.Set();
            ExitEvent.Reset();
        }
        public delegate void DelSubscribe();
        object lconns = new object();
        object lconn = new object();

        void subscribe()
        { }
        public async void Connect()
        {
            ClientWebSocket ws = new ClientWebSocket();
            var URL = new Uri("ws://" + socSet.url + ":" + socSet.port);

            Logger.Info($"UMDServerConnect: {socSet.name}");

            await ConnectAsync(rm, socSet);
            //await ws.ConnectAsync(URL, CancellationToken.None);
        }
        void rmconn(ClientToServerConnection that)
        {
            bool locked = false;
            try
            {
                Logger.Info($"UMDServerConnect: Remove Connection {name}");
                Monitor.TryEnter(lconns, 500, ref locked);
                if (locked)
                {
                    that.oclient = null;
                    that.timer.Dispose();
                    conns.Remove(that);
                }
                else
                    throw new Exception($"Deadlock removing a connection to {name}");
            }
            catch (Exception e) { }
            finally { if(locked) Monitor.Exit(lconns); }
        }

        void addconn(ClientToServerConnection that)
        {
            bool locked = false;
            try
            {
                Logger.Info("UMDServerConnect: Add Connection" + name);
                Monitor.TryEnter(lconns, 500, ref locked);
                if (locked)
                {
                    that.timer = new Timer(SendPing, that, 2000, 15000);
                    conns.Add(that);
                    //timer = new Timer(SendPing, this, 5000, 10000);
                }
                else
                    throw new Exception("Deadlock adding a connection to a WinsysDB");
            }
            catch (Exception e) { }
            finally { if(locked) Monitor.Exit(lconns);}
        }
        TimeSpan ts = new TimeSpan();
        private async Task ConnectAsync(ReceiveMsgDelegate mp, SocketSettings socSet, WaitHandle connwh=null)
        {
            AppDomain.CurrentDomain.ProcessExit += CurrentDomainOnProcessExit;
            Logger.Info("====================================");
            Logger.Info($"      STARTING WEBSOCKET {socSet.name}    ");
            Logger.Info("        Manager API Client          ");
            Logger.Info("====================================");

            var factory = new Func<ClientWebSocket>(() => new ClientWebSocket
            {
                Options =
                {
                    KeepAliveInterval = TimeSpan.FromSeconds(socSet.keepalive),
                    // Proxy = ...
                    // ClientCertificates = ...
                }
            });

            if (!bRunning)
            {
                try
                {
                    bRunning = true;
                    addconn(this);
                    var URL = new Uri("ws://" + socSet.url + ":" + socSet.port);
                    using (IWebsocketClient client = new WebsocketClient(URL, factory))
                    {
                        string received = null;
                        oclient = client;
                        client.Name = name;
                        client.ReconnectTimeoutMs = (int)TimeSpan.FromSeconds(socSet.recontimeout).TotalMilliseconds;
                        client.ErrorReconnectTimeoutMs = (int)TimeSpan.FromSeconds(socSet.errrecontimeout).TotalMilliseconds;

                        client.ReconnectionHappened.Subscribe((Action<ReconnectionType>)((type) =>
                         {
                             Logger.Info($"Reconnecting Manager API Client to type: {type} , url: {client.Url} , name: {name} {client.Name}");
                         }));

                        client.DisconnectionHappened.Subscribe((Action<DisconnectionType>)((type) =>
                        {
                            Logger.Info($"Disconnection Manager API Client to type: {type} , url: {client.Url}, name {name} {client.Name}");
                        }));
                        client.MessageReceived.Subscribe((msg) =>
                        {
                            try
                            {
                                if (msg.Text != null)
                                {
                                    received = msg.Text;
                                    Logger.Debug($"Message To Manager API Client From : {received}, name {name} {client.Name}");
                                }
                                else if (msg.Binary != null)
                                {
                                    isaCommand cmd = MsgProcessor.CommandFactory(msg.Binary);

                                    if (cmd.command == eCommand.Ping)
                                    {
                                        Logger.Debug($"Ping To Manager API Client, Reply Pong to {name} {client.Name}");
                                        client.Send(new Command() { command = eCommand.Pong }.ToArray());
                                    }
                                    else if (cmd.command == eCommand.Pong)
                                        Logger.Debug($"Manager API Received Pong from {name} {client.Name}");
                                    else
                                    {
                                        Logger.Info($"Manager API Received msg from {name} {client.Name}");
                                        mp(cmd);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                //isaCommand cmd = MsgProcessor.CommandFactory(msg.Binary);
                                Logger.Error($"Message Received Manager API: " + ex.Message + Environment.NewLine + msg.ToString());
                            }
                        });
                        await client.Start();

                        //new Thread(() => StartSendingPing(client)).Start();
                        ExitEvent.WaitOne();
                    }
                }
                catch (Exception e) { }
                finally {
                    rmconn(this);
                    if(connwh!=null)
                        ((AutoResetEvent)connwh).Set();
                }
            }
            Logger.Info("====================================");
            Logger.Info($"   STOPPING WebSocketClient {name} ");
            Logger.Info("      Manager API Client            ");
            Logger.Info("====================================");
        }

        bool recon = false;

        private static void SendPing(object state)
        {
            ClientToServerConnection conn = (ClientToServerConnection)state;
            
            Logger.Debug($"Manager API sending Ping to {conn.name}");

            if (conn.bRunning && conns.Contains(conn))
            {
                if (conn.oclient == null)
                    conn.bRunning = false;
                else
                {
                    WebsocketClient ws = ((MobileDeliveryClient.WebsocketClient)conn.oclient);
                    if (ws.IsRunning)
                    {
                        Task snd = conn.oclient.Send(new Command() { command = eCommand.Ping }.ToArray());

                        if (snd.Status == TaskStatus.Faulted && !conn.recon)
                        {
                            conn.recon = true;
                            Logger.Info($"Server Disconnected, reconnect! {conn.name}");
                            // Disconnect();
                            Thread.Sleep(600);
                            if (!conn.bRunning)
                                conn.StartAsync();
                            Logger.Info($"Server Disconnected Done reconnecting! {conn.name}");
                            conn.recon = false;
                        }
                    }
                    else if (!conn.recon)
                    {
                        conn.recon = true;
                        Logger.Info($"Server Disconnected, reconnect! {conn.name}");
                        conn.StartAsync();
                        Thread.Sleep(600);
                        Logger.Info($"Server Disconnected Done reconnecting! {conn.name}");
                        conn.recon = false;
                    }
                }
            }
            else
            { conn.bRunning = false; }
        }

      
        private static async Task SwitchUrl(IWebsocketClient client)
        {
            while (true)
            {
                await Task.Delay(10000);
                var production = new Uri("wss://localhost:8181");
                var testnet = new Uri("wss://localhost:8182");
                var selected = client.Url == production ? testnet : production;
                client.Url = selected;
                await client.Reconnect();
            }
        }

        private static void CurrentDomainOnProcessExit(object sender, EventArgs eventArgs)
        {
            Logger.Warn("Exiting process");
            DisconnectAll();
        }
        static List<ClientToServerConnection> conns = new List<ClientToServerConnection>();
        private static void ConsoleOnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            Logger.Warn("Canceling process");
            e.Cancel = true;
            DisconnectAll();
        }
        static void DisconnectAll()
        {
            foreach( var itcon in conns)
            {
                itcon.Disconnect();
            }
        }
    }
}
