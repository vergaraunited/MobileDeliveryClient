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
using MobileDeliverySettings.Settings;

namespace MobileDeliveryClient.API
{
    public delegate void ev_name_hook(string name);

    public class ClientToServerConnection
    {
        private readonly ManualResetEvent ExitEvent = new ManualResetEvent(false);
        //static Dictionary<int, IWebsocketClient> dClients = new Dictionary<int, IWebsocketClient>();
        string url { get; set; }
        ushort port { get; set; }
        public string Name;
        public string name { get { return Name; } private set { Name = value; if (ev_name != null) ev_name(Name); } }

        event ev_name_hook ev_name;
        public class clientClass
        {
            public Semaphore sem;
            public IWebsocketClient client;
            public Timer timer;
        }
        static Dictionary<int, clientClass> dLocks = new Dictionary<int, clientClass>();
        public bool IsConnected
        {
            get { return bRunning; }
        }
        SocketSettings socSet;

        ReceiveMsgDelegate rm;
        isaSendMessageCallback sm;
        IWebsocketClient oclient;
        Timer timer;
        //DateTime lstMsgSent=DateTime.Now;

        private bool bRunning { get; set; }
        private bool bStopped { get; set; }
        
        public ClientToServerConnection(SocketSettings socSet, ref SendMsgDelegate sm, ReceiveMsgDelegate rm, ev_name_hook ev=null)
        {
            if (ev != null)
                Subscribe(ev);

            this.url = socSet.url;
            this.port = socSet.port;
            this.name = socSet.name;
            Logger.Info($"{name} Client Connectng to server Connection: ws://{url}:{port}");
            this.socSet = socSet;
            this.rm = rm;
            sm = new SendMsgDelegate(SendCommandToServer);
            Logger.AppName = socSet.name;
        }
        public void Subscribe(ev_name_hook ev) {
            if (ev_name == null)
                ev_name = new ev_name_hook(ev);
            else
                ev_name += ev;
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
                    StartAsync();
                    Thread.Sleep(1000);
                }
            }
            Logger.Info($"UMDServerConnection SendMsgEventAsync {name} {cmd.ToString()}");
            if (oclient != null)
            {
                Task retTsk = oclient.Send(cmd.ToArray());
                if (timer != null)
                    timer.Change(200, 10000);
                if (retTsk.Status == TaskStatus.Faulted)
                    Logger.Info($"UMDServerConnection:Task SendMsgEventAsync: faulted {name}  " + cmd.ToString());
            }
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
                    if (!bRunning && !bStopped)
                    {
                        bRunning = true;
                        WaitHandle connectedwh = new AutoResetEvent(initialState: false);
                        
                        connectionTask = new Task(async () => await ConnectAsync(rm, socSet, connectedwh));
                        connectionTask.Start();
                        
                        bool signaled = connectedwh.WaitOne(10000);
                        if (signaled)
                        {
                            Logger.Info("Connected");
                            bRunning = true;
                            //timer = new Timer(SendPing, this, 5000, 10000);
                        }
                        else
                        {
                            bRunning = false;
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

           // int cnt = 0;
            //while (bRunning && cnt++<=10)
            //    Thread.Sleep(100);
            ExitEvent.Set();
            
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
                Logger.Info($"UMDServerConnect: Remove Connection {that.socSet.name}");
                //lock (olock)

                Monitor.TryEnter(lconns, 500, ref locked);
                if (locked)
                {
                    ExitEvent.Reset();
                  //  that.oclient = null;
                    dLocks[that.socSet.port].timer.Dispose();
                    //conns.Remove(that);
                    dLocks[that.socSet.port].sem.Release();
                    //if (dClients.ContainsKey(that.socSet.port))
                    //    dClients.Remove(that.socSet.port);
                    
                }
                else
                    throw new Exception($"Deadlock removing a connection to {that.socSet.name}");
            }
            catch (Exception e) { Logger.Error($"Removing Connection error: {that.socSet.name}.", e); }
            finally { if(locked) Monitor.Exit(lconns); }
        }

        bool addconn(IWebsocketClient client)
        {
            try
            {
                Logger.Info("UMDServerConnect: Add Connection" + name);

                if (!dLocks.ContainsKey(socSet.port))
                {
                    int ctm = Int32.Parse(MobileDeliverySettings.Settings.UMDAppConfig.dSettings["KeepAliveInterval"]);
                    int tm =  ctm / 2;
                    var timer = new Timer(SendPing, client, 2000, tm);
                    bRunning = true;
                    dLocks.Add(client.Url.Port, new clientClass() { sem = new Semaphore(1, 1, client.Url.ToString()), client=client, timer=timer } );
                    return true;
                }
                else
                    bRunning = false;
            }
            catch (Exception e) { Logger.Error($"ClientToServerConnection: Add Connection {name}.", e); }
            return false;
        }
        TimeSpan ts = new TimeSpan();
        private async Task ConnectAsync(ReceiveMsgDelegate mp, SocketSettings socSet, WaitHandle connwh=null)
        {
            AppDomain.CurrentDomain.ProcessExit += CurrentDomainOnProcessExit;
            Logger.Info("====================================");
            Logger.Info($"      STARTING WEBSOCKET {socSet.name}    ");
            Logger.Info("        Manager API Client          ");
            Logger.Info("====================================");

            Func<ClientWebSocket> factory;

            factory = socSet.name.ToUpper().Contains("VM") ?
                new Func<ClientWebSocket>(() => new ClientWebSocket
                {
                    Options = { KeepAliveInterval = TimeSpan.FromMilliseconds(socSet.keepalive) }
                }) :
                new Func<ClientWebSocket>(() => new ClientWebSocket {
                    Options = { KeepAliveInterval = TimeSpan.FromMilliseconds(socSet.keepalive) }
                });

            try
            {

                if (!bRunning)
                {
                    if (dLocks.ContainsKey(socSet.port) && (!(dLocks.ContainsKey(socSet.port) ^ dLocks[socSet.port].sem.WaitOne(50))))
                        return;

                    bRunning = true;
                    var URL = new Uri("ws://" + socSet.url + ":" + socSet.port);
                    using (IWebsocketClient client = new WebsocketClient(URL, factory))
                    {
                        string received = null;
                        oclient = client;
                        this.port = (ushort)client.Url.Port;
                        this.url = client.Url.OriginalString;
                        client.Name = name + "(" + this.port.ToString() + ")";
                        this.name = client.Name;
                        client.ReconnectTimeoutMs = (int)TimeSpan.FromMilliseconds(socSet.recontimeout).TotalMilliseconds;
                        client.ErrorReconnectTimeoutMs = (int)TimeSpan.FromMilliseconds(socSet.errrecontimeout).TotalMilliseconds;

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
                                    this.name = $"{client.Name} {received}";
                                    Console.Title = this.name;
                                }
                                else if (msg.Binary != null)
                                {
                                    isaCommand cmd = MsgProcessor.CommandFactory(msg.Binary);

                                    if (cmd.command == eCommand.Ping)
                                    {
                                        Logger.Debug($"Client Got Pinged - Ping To Manager API Client, Reply Pong to {name} {client.Name}");
                                        client.Send(new Command() { command = eCommand.Pong }.ToArray());
                                    }
                                    else if (cmd.command == eCommand.Pong)
                                        Logger.Debug($"Client Got Ponged - Manager API Received Pong from {name} {client.Name} {client}");
                                    else
                                    {
                                        Logger.Info($"Client Msg received - Manager API Received msg from {name} {client.Name}");
                                        mp(cmd);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                //isaCommand cmd = MsgProcessor.CommandFactory(msg.Binary);
                                Logger.Error($"Client Message Received Manager API: " + ex.Message + Environment.NewLine + msg.ToString());
                            }
                        });

                        await client.Start();

                        if (connwh != null)
                            ((AutoResetEvent)connwh).Set();

                        if (!addconn(client))
                            return;

                        //new Thread(() => StartSendingPing(client)).Start();
                        ExitEvent.WaitOne();
                    }
                }
            }
            catch (Exception e) { }
            finally
            {
                rmconn(this);
                bRunning = false;
            }
            Logger.Info("====================================");
            Logger.Info($"   STOPPING WebSocketClient {name} ");
            Logger.Info("      Manager API Client            ");
            Logger.Info("====================================");
        }

        Object olock = new object();
        object reconlock = new object();


        private static void SendPing(object state)
        {
            Logger.Debug($"Sending PING"); 

            IWebsocketClient client = (IWebsocketClient)state;

            if (client == null)
                return;

            if (client != null)
            { 
                var ts = DateTime.Now.ToUniversalTime().TimeOfDay.Subtract(client.lstMsgRcvd.ToUniversalTime().TimeOfDay).TotalMilliseconds;
                Logger.Debug($"Sending PING {client.Name} timespan = {client.lstMsgRcvd.ToUniversalTime().TimeOfDay} - {DateTime.Now.ToUniversalTime().TimeOfDay} = {ts}");
                if (ts > Int32.Parse(MobileDeliverySettings.Settings.UMDAppConfig.dSettings["KeepAliveInterval"])/2)
                {
                    if (client.IsRunning)
                    {
                        Task snd = client.Send(new Command() { command = eCommand.Ping }.ToArray());

                        if (snd.Status == TaskStatus.RanToCompletion)
                        {
                            Logger.Info($"Client Ping Sent Succesfully! {client.Name} {client.Url}");
                            return;
                        }
                        else if (snd.Status == TaskStatus.Faulted)// && !conn.recon)
                        {
                            Logger.Info($"Client Ping Faulted - Server Connection Defaulted, reconnect! {client.Name} {client.Url}");
                        }
                    }
                    else if (client.IsReconnectionEnabled)
                    {
                        Logger.Info($"Client Ping Failed - Server Disconnected IsReconnectionEnabled , reconnecting?! {client.Name} {client.Url}");
                    }
                    else 
                    {
                        Logger.Info($"Client Ping Failed - Client not running / Disconnected, reconnect! {client.Name} {client.Url}");
                    }
                    client.Reconnect();
                }
            }
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
            //DisconnectAll();
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
