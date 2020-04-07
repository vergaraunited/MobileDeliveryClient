using MobileDeliveryLogger;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using MobileDeliveryGeneral.Settings;
using MobileDeliveryGeneral.Utilities;
using MobileDeliveryGeneral.Interfaces;
using static MobileDeliveryGeneral.Definitions.MsgTypes;
using MobileDeliveryClient.Interfaces;
using MobileDeliveryClient.MessageTypes;

namespace MobileDeliveryClient.API
{
    public class UMDServerConnection
    {
        private static readonly ManualResetEvent ExitEvent = new ManualResetEvent(false);
        
        public string url { get; set; }
        ushort port { get; set; }
        string name { get; set;}

        ReceiveMsgDelegate rm;
        isaSendMessageCallback sm;
        IWebsocketClient oclient;

        private bool bRunning { get; set; }
        private bool bStopped { get; set; }
        
        public UMDServerConnection(SocketSettings set, ref SendMsgDelegate sm, ReceiveMsgDelegate rm)
        {
            Logger.Info($"{set.name} Server Connection: ws://{set.url}:{set.port}");
            url = set.url;
            port = set.port;
            name = set.name;
            this.rm = rm;
            sm = new SendMsgDelegate(SendCommand);
            Logger.AppName = name;
        }

        public UMDServerConnection(SocketSettings set)
        {
            url = set.WinSysUrl;
            port = set.WinSysPort;
            name = set.name;
            Logger.AppName = name;
        }

        void RecvdMsgEvent(isaCommand rcvMsg)
        {
            rm(rcvMsg);
            Logger.Debug("Received Message Event");
        }

        public bool SendCommand(isaCommand inputparam)
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
                Logger.Info("UMDServerConnection:Task SendMsgEventAsync: Client not connected");
                if (!bStopped)
                {
                    bRunning = false;
                    //StartAsync();
                    Thread.Sleep(100);
                }
            }
            Logger.Info("UMDServerConnection SendMsgEventAsync {name} " + cmd.ToString());
            Task retTsk = oclient.Send(cmd.ToArray());
            if (retTsk.Status == TaskStatus.Faulted)
            {
                Console.WriteLine("UMDServerConnection SendMsgEventAsync: faulted {name} " + cmd.ToString());
                Logger.Info("UMDServerConnection:Task SendMsgEventAsync: faulted {name}  " + cmd.ToString());
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
                    if (!bRunning)
                    {
                        WaitHandle connectedwh = new AutoResetEvent(initialState: false);
                        
                        connectionTask = new Task(async () => await ConnectAsync(rm, name, connectedwh));
                        connectionTask.Start();

                        bool signaled = connectedwh.WaitOne(5000);
                        if (signaled)
                        {
                            Logger.Info("Connected");
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
            Logger.Debug("Disconnecting {name}.");
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
            var URL = new Uri("ws://" + url + ":" + port);

            Logger.Debug($"UMDServerConnect: {name}");

            await ConnectAsync(rm, name);
            //await ws.ConnectAsync(URL, CancellationToken.None);
        }
        void rmconn(UMDServerConnection that)
        {
            bool locked = false;
            try
            {
                Logger.Debug($"UMDServerConnect: Remove Connection {name}");
                Monitor.TryEnter(lconns, 500, ref locked);
                if (locked)
                {
                    oclient = null;
                    conns.Remove(that);
                }
                else
                    throw new Exception($"Deadlock removing a connection to {name}");
            }
            catch (Exception e) { }
            finally { if(locked) Monitor.Exit(lconns); }
        }

        void addconn(UMDServerConnection that)
        {
            bool locked = false;
            try
            {
                Logger.Debug("UMDServerConnect: Add Connection" + name);
                Monitor.TryEnter(lconns, 500, ref locked);
                if (locked)
                {
                    conns.Add(that);
                }
                else
                    throw new Exception("Deadlock adding a connection to a WinsysDB");
            }
            catch (Exception e) { }
            finally { if(locked) Monitor.Exit(lconns);}
        }
        
        private async Task ConnectAsync(ReceiveMsgDelegate mp, string name, WaitHandle connwh=null)
        {
            AppDomain.CurrentDomain.ProcessExit += CurrentDomainOnProcessExit;
            Logger.Debug("====================================");
            Logger.Debug($"      STARTING WEBSOCKET {name}    ");
            Logger.Debug("        Manager API Client          ");
            Logger.Debug("====================================");

            var factory = new Func<ClientWebSocket>(() => new ClientWebSocket
            {
                Options =
                {
                    KeepAliveInterval = TimeSpan.FromSeconds(5),
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
                    var URL = new Uri("ws://" + url + ":" + port);
                    using (IWebsocketClient client = new WebsocketClient(URL, factory))
                    {
                        string received = null;
                        oclient = client;
                        client.Name = name;
                        client.ReconnectTimeoutMs = (int)TimeSpan.FromSeconds(60).TotalMilliseconds;
                        client.ErrorReconnectTimeoutMs = (int)TimeSpan.FromSeconds(60).TotalMilliseconds;
                        
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
                                        Logger.Debug($"Manager API Received msg from {name} {client.Name}");
                                        mp(cmd);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                //isaCommand cmd = MsgProcessor.CommandFactory(msg.Binary);
                                Logger.Error($"Message Received Manager API: " + ex.Message + " " + msg.ToString());
                            }
                        });
                        await client.Start();
                        new Thread(() => StartSendingPing(client)).Start();
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
            Logger.Debug("====================================");
            Logger.Debug($"   STOPPING WebSocketClient {name} ");
            Logger.Debug("      Manager API Client            ");
            Logger.Debug("====================================");
        }
        
        private void StartSendingPing(IWebsocketClient client)
        {
            bStopped = false;
            bool recon = false;
            while (bRunning)
            {
                Thread.Sleep(5000);
                Logger.Debug($"Manager API sending Ping to {name}");

                if (bRunning && conns.Contains(this))
                {
                    Task snd = client.Send(new Command() { command = eCommand.Ping }.ToArray());

                    if (snd.Status == TaskStatus.Faulted && !recon)
                    {
                        recon = true;
                        Logger.Debug($"Server Disconnected! {name}");
                        Disconnect();
                        Thread.Sleep(5000);
                        if (!bRunning)
                            StartAsync();
                        else
                            return;
                    }
                    while (snd.Status == TaskStatus.Running)
                        Thread.Sleep(10);
                }
                else
                { bRunning = false; rmconn(this); }
            }
            bStopped=true;
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
        static List<UMDServerConnection> conns = new List<UMDServerConnection>();
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
