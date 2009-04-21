﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.Diagnostics;
using Tibia.Packets;
using Tibia.Objects;
using System.Windows.Forms;
using System.Net;
using System.Threading;

namespace Tibia.Packets
{
    public class Proxy : Util.ProxyBase
    {
        #region Vars

        private static byte[] localHostBytes = new byte[] { 127, 0, 0, 1 };
        private static Random random = new Random();

        private Client client;

        private NetworkMessage clientRecvMsg, serverRecvMsg;
        private NetworkMessage clientSendMsg, serverSendMsg;

        private bool isOtServer;

        private LoginServer[] loginServers;
        private CharacterLoginInfo[] charList;
        private ushort loginServerPort, worldServerPort;

        private uint[] xteaKey;

        private Protocol protocol = Protocol.None;

        private int selectedLoginServer = 0;

        private TcpListener loginClientTcp, worldClientTcp;
        private Socket clientSocket;
        private NetworkStream clientStream;
        private Queue<byte[]> clientSendQueue;
        private object clientSendQueueLock;
        private bool clientWriting;
        private Thread clientSendThread;
        private object clientSendThreadLock;

        private bool accepting = false;
        private object restartLock;

        private TcpClient serverTcp;
        private NetworkStream serverStream;
        private Queue<byte[]> serverSendQueue;
        private object serverSendQueueLock;
        private bool serverWriting;
        private Thread serverSendThread;
        private object serverSendThreadLock;

        private object debugLock;
        private bool connected;

        private byte lastRecvPacketType;

        private DateTime lastInteraction;
        #endregion

        #region Event Handlers

        private bool Proxy_ReceivedSelfAppearIncomingPacket(IncomingPacket packet)
        {
            connected = true;

            if (PlayerLogin != null)
                Util.Scheduler.AddTask(PlayerLogin, new object[] { this, new EventArgs() }, 1000);

            return true;
        }

        #endregion

        #region Events

        public event EventHandler PlayerLogin;
        public event EventHandler PlayerLogout;

        #endregion

        #region Properties

        public uint[] XteaKey
        {
            get { return xteaKey; }
        }

        #endregion

        #region Public Functions

        public void CheckState()
        {
            if ((DateTime.Now - lastInteraction).TotalSeconds >= 30)
            {
                Restart();
            }
        }

        public void SendToServer(byte[] data)
        {
            lock (serverSendQueueLock)
            {
                serverSendQueue.Enqueue(data);
            }

            lock (serverSendThreadLock)
            {
                if (!serverWriting)
                {
                    serverWriting = true;
                    serverSendThread = new Thread(new ThreadStart(ServerSend));
                    serverSendThread.Start();
                }
            }
        }

        public void SendToClient(byte[] data)
        {
            lock (clientSendQueueLock)
            {
                clientSendQueue.Enqueue(data);
            }

            lock (clientSendThreadLock)
            {
                if (!clientWriting)
                {
                    clientWriting = true;
                    clientSendThread = new Thread(new ThreadStart(ClientSend));
                    clientSendThread.Start();
                }
            }
        }

        #endregion

        #region Constructor

        public Proxy(Client client)
        {
            try
            {
                this.client = client;
                Initialize();
            }
            catch (Exception ex)
            {
                WriteDebug(ex.Message + "\nStackTrace: " + ex.StackTrace);
            }
        }

        private void Initialize()
        {
            serverRecvMsg = new NetworkMessage(client);
            clientRecvMsg = new NetworkMessage(client);
            clientSendMsg = new NetworkMessage(client);
            serverSendMsg = new NetworkMessage(client);

            clientSendQueue = new Queue<byte[]>();
            serverSendQueue = new Queue<byte[]>();

            clientSendQueueLock = new object();
            serverSendQueueLock = new object();
            restartLock = new object();

            clientSendThreadLock = new object();
            serverSendThreadLock = new object();

            debugLock = new object();

            xteaKey = new uint[4];

            loginServers = client.Login.Servers;

            if (loginServers[0].Server == "localhost")
                loginServers = client.Login.DefaultServers;

            if (loginServerPort == 0)
                loginServerPort = GetFreePort();

            worldServerPort = (ushort)(loginServerPort + 1);

            client.Login.SetServer("localhost", (short)loginServerPort);

            if (client.Login.RSA == Constants.RSAKey.OpenTibia)
                isOtServer = true;
            else
            {
                client.Login.RSA = Constants.RSAKey.OpenTibia;
                isOtServer = false;
            }

            if (client.Login.CharListCount != 0)
            {
                charList = client.Login.CharacterList;
                client.Login.SetCharListServer(localHostBytes, loginServerPort);
            }

            //login event
            ReceivedSelfAppearIncomingPacket += new IncomingPacketListener(Proxy_ReceivedSelfAppearIncomingPacket);

            StartListenFromClient();
            client.IO.UsingProxy = true;
        }

        #endregion

        #region Main Flow

        private void StartListenFromClient()
        {
            try
            {
                accepting = true;
                protocol = Protocol.None;
                clientSendQueue.Clear();
                serverSendQueue.Clear();

                loginClientTcp = new TcpListener(IPAddress.Any, loginServerPort);
                loginClientTcp.Start();
                loginClientTcp.BeginAcceptSocket(new AsyncCallback(ListenClientCallBack), 0);

                worldClientTcp = new TcpListener(IPAddress.Any, worldServerPort);
                worldClientTcp.Start();
                worldClientTcp.BeginAcceptSocket(new AsyncCallback(ListenClientCallBack), 1);
            }
            catch (Exception ex)
            {
                WriteDebug(ex.Message + "\nStackTrace: " + ex.StackTrace);
            }
        }

        private void ListenClientCallBack(IAsyncResult ar)
        {
            try
            {
                if (!accepting)
                    return;

                accepting = false;
                clientSocket = loginClientTcp.EndAcceptSocket(ar);

                loginClientTcp.Stop();
                worldClientTcp.Stop();

                int type = (int)ar.AsyncState;
                //we have to connect to the world server now.. and send w8 for response..
                if (type == 1)
                {
                    serverTcp = new TcpClient(BitConverter.GetBytes(charList[client.Login.SelectedChar].WorldIP).ToIPString(), charList[client.Login.SelectedChar].WorldPort);
                    serverStream = serverTcp.GetStream();
                    serverStream.BeginRead(serverRecvMsg.GetBuffer(), 0, 2, new AsyncCallback(ServerReadCallBack), null);
                }

                clientStream = new NetworkStream(clientSocket);
                clientStream.BeginRead(clientRecvMsg.GetBuffer(), 0, 2, new AsyncCallback(ClientReadCallBack), null);
            }
            //catch (ObjectDisposedException) { Restart(); }
            //catch (System.IO.IOException) { Restart(); }
            catch (Exception ex)
            {
                WriteDebug(ex.Message + "\nStackTrace: " + ex.StackTrace);
                Restart();
            }
        }

        private void ClientReadCallBack(IAsyncResult ar)
        {
            try
            {
                int read = clientStream.EndRead(ar);

                if (read < 2)
                {
                    Restart();
                    return;
                }

                int pSize = (int)BitConverter.ToUInt16(clientRecvMsg.GetBuffer(), 0) + 2;

                while (read < pSize)
                {
                    if (clientStream.CanRead)
                        read += clientStream.Read(clientRecvMsg.GetBuffer(), read, pSize - read);
                    else
                    {
                        throw new Exception("Connection broken.");
                    }
                }

                clientRecvMsg.Length = pSize;

                switch (protocol)
                {
                    case Protocol.None:
                        ParseFirstClientMsg();
                        break;
                    case Protocol.World:
                        OnReceivedDataFromClient(clientRecvMsg.Data);

                        if (clientRecvMsg.CheckAdler32() && clientRecvMsg.XteaDecrypt(xteaKey))
                        {

                            clientRecvMsg.Position = 6;
                            int msgLength = (int)clientRecvMsg.GetUInt16() + 8;
                            serverSendMsg.Reset();


                            int position = clientRecvMsg.Position;

                            if (!ParsePacketFromClient(client, clientRecvMsg, serverSendMsg))
                            {
                                //unknown packet
                                byte[] unknown = clientRecvMsg.GetBytes(clientRecvMsg.Length - clientRecvMsg.Position);

                                OnSplitPacketFromClient(unknown[0], unknown);

                                WriteDebug("Unknown outgoing packet: " + unknown.ToHexString());
                                serverSendMsg.AddBytes(unknown);
                            }

                            byte[] data = new byte[clientRecvMsg.Position - position];
                            Array.Copy(clientRecvMsg.GetBuffer(), position, data, 0, data.Length);
                            OnSplitPacketFromClient(data[0], data);

                            if (serverSendMsg.Length > 8)
                            {
                                serverSendMsg.InsetLogicalPacketHeader();
                                serverSendMsg.XteaEncrypt(xteaKey);
                                serverSendMsg.AddAdler32();
                                serverSendMsg.InsertPacketHeader();

                                lock (serverSendQueueLock)
                                {
                                    serverSendQueue.Enqueue(serverSendMsg.Data);
                                }

                                lock (serverSendThreadLock)
                                {
                                    if (!serverWriting)
                                    {
                                        serverWriting = true;
                                        serverSendThread = new Thread(new ThreadStart(ServerSend));
                                        serverSendThread.Start();
                                    }
                                }
                            }
                        }
                        clientStream.BeginRead(clientRecvMsg.GetBuffer(), 0, 2, new AsyncCallback(ClientReadCallBack), null);
                        break;
                    case Protocol.Login:
                        break;
                }
            }
            catch (ObjectDisposedException ex) { WriteDebug(ex.Message + "\nStackTrace: " + ex.StackTrace); }
            catch (System.IO.IOException ex) { WriteDebug(ex.Message + "\nStackTrace: " + ex.StackTrace); }
            catch (Exception ex)
            {
                WriteDebug(ex.Message + "\nStackTrace: " + ex.StackTrace);
                Restart();
            }
        }

        private void ParseFirstClientMsg()
        {
            try
            {
                clientRecvMsg.Position = 6;
                byte protocolId = clientRecvMsg.GetByte();
                int position;

                switch (protocolId)
                {
                    case 0x01:

                        protocol = Protocol.Login;
                        clientRecvMsg.GetUInt16();
                        ushort clientVersion = clientRecvMsg.GetUInt16();

                        clientRecvMsg.GetUInt32();
                        clientRecvMsg.GetUInt32();
                        clientRecvMsg.GetUInt32();

                        position = clientRecvMsg.Position;

                        clientRecvMsg.RsaOTDecrypt();

                        if (clientRecvMsg.GetByte() != 0)
                        {
                            Restart();
                            return;
                        }

                        xteaKey[0] = clientRecvMsg.GetUInt32();
                        xteaKey[1] = clientRecvMsg.GetUInt32();
                        xteaKey[2] = clientRecvMsg.GetUInt32();
                        xteaKey[3] = clientRecvMsg.GetUInt32();

                        if (clientVersion != Version.CurrentVersion)
                        {
                        }

                        serverTcp = new TcpClient(loginServers[selectedLoginServer].Server, loginServers[selectedLoginServer].Port);
                        serverStream = serverTcp.GetStream();

                        if (isOtServer)
                            clientRecvMsg.RsaOTEncrypt(position);
                        else
                            clientRecvMsg.RsaCipEncrypt(position);

                        clientRecvMsg.AddAdler32();
                        clientRecvMsg.InsertPacketHeader();

                        serverStream.Write(clientRecvMsg.GetBuffer(), 0, clientRecvMsg.Length);
                        serverStream.BeginRead(serverRecvMsg.GetBuffer(), 0, 2, new AsyncCallback(ServerReadCallBack), null);
                        break;

                    case 0x0A:

                        protocol = Protocol.World;
                        clientRecvMsg.GetUInt16();
                        clientRecvMsg.GetUInt16();


                        position = clientRecvMsg.Position;

                        clientRecvMsg.RsaOTDecrypt();

                        if (clientRecvMsg.GetByte() != 0)
                        {
                            Restart();
                            return;
                        }

                        xteaKey[0] = clientRecvMsg.GetUInt32();
                        xteaKey[1] = clientRecvMsg.GetUInt32();
                        xteaKey[2] = clientRecvMsg.GetUInt32();
                        xteaKey[3] = clientRecvMsg.GetUInt32();

                        if (isOtServer)
                            clientRecvMsg.RsaOTEncrypt(position);
                        else
                            clientRecvMsg.RsaCipEncrypt(position);

                        clientRecvMsg.AddAdler32();
                        clientRecvMsg.InsertPacketHeader();

                        serverStream.Write(clientRecvMsg.GetBuffer(), 0, clientRecvMsg.Length);

                        serverStream.BeginRead(serverRecvMsg.GetBuffer(), 0, 2, new AsyncCallback(ServerReadCallBack), null);
                        clientStream.BeginRead(clientRecvMsg.GetBuffer(), 0, 2, new AsyncCallback(ClientReadCallBack), null);

                        break;

                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                Restart();
                WriteDebug(ex.Message + "\nStackTrace: " + ex.StackTrace);
            }
        }

        private void ServerReadCallBack(IAsyncResult ar)
        {
            try
            {
                int read = serverStream.EndRead(ar);

                if (read < 2)
                {
                    Restart();
                    return;
                }

                lastInteraction = DateTime.Now;
                int pSize = (int)BitConverter.ToUInt16(serverRecvMsg.GetBuffer(), 0) + 2;

                while (read < pSize)
                {
                    if (serverStream.CanRead)
                        read += serverStream.Read(serverRecvMsg.GetBuffer(), read, pSize - read);
                    else
                    {
                        throw new Exception("Connection broken.");
                    }
                }

                serverRecvMsg.Length = pSize;

                switch (protocol)
                {
                    case Protocol.Login:
                        ParseCharacterList();
                        break;
                    case Protocol.World:
                        OnReceivedDataFromServer(serverRecvMsg.Data);

                        if (serverRecvMsg.CheckAdler32() && serverRecvMsg.XteaDecrypt(xteaKey))
                        {

                            serverRecvMsg.Position = 6;
                            int msgSize = (int)serverRecvMsg.GetUInt16() + 8;
                            clientSendMsg.Reset();

                            while (serverRecvMsg.Position < msgSize)
                            {
                                int position = serverRecvMsg.Position;

                                if (!ParsePacketFromServer(client, serverRecvMsg, clientSendMsg))
                                {
                                    byte[] unknown = serverRecvMsg.GetBytes(serverRecvMsg.Length - serverRecvMsg.Position);

                                    OnSplitPacketFromServer(unknown[0], unknown);

                                    WriteDebug("Last recv packet type: " + lastRecvPacketType.ToString("X") + " Unknown incoming packet: " + unknown.ToHexString());
                                    clientSendMsg.AddBytes(unknown);
                                    break;
                                }

                                lastRecvPacketType = serverRecvMsg.GetBuffer()[position];

                                byte[] data = new byte[serverRecvMsg.Position - position];
                                Array.Copy(serverRecvMsg.GetBuffer(), position, data, 0, data.Length);

                                OnSplitPacketFromServer(data[0], data);
                            }

                            if (clientSendMsg.Length > 8)
                            {
                                clientSendMsg.InsetLogicalPacketHeader();
                                clientSendMsg.XteaEncrypt(xteaKey);
                                clientSendMsg.AddAdler32();
                                clientSendMsg.InsertPacketHeader();

                                lock (clientSendQueueLock)
                                {
                                    clientSendQueue.Enqueue(clientSendMsg.Data);
                                }

                                lock (clientSendThreadLock)
                                {
                                    if (!clientWriting)
                                    {
                                        clientWriting = true;
                                        clientSendThread = new Thread(new ThreadStart(ClientSend));
                                        clientSendThread.Start();
                                    }
                                }
                            }
                        }

                        serverStream.BeginRead(serverRecvMsg.GetBuffer(), 0, 2, new AsyncCallback(ServerReadCallBack), null);
                        break;
                    case Protocol.None:
                        SendToClient(serverRecvMsg.Data);
                        break;
                }
            }
            catch (System.IO.IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                WriteDebug(ex.Message + "\nStackTrace: " + ex.StackTrace);
                Restart();
            }
        }

        private void ParseCharacterList()
        {
            try
            {
                if (serverRecvMsg.CheckAdler32() && serverRecvMsg.PrepareToRead())
                {
                    serverRecvMsg.GetUInt16();

                    while (serverRecvMsg.Position < serverRecvMsg.Length)
                    {
                        byte cmd = serverRecvMsg.GetByte();

                        switch (cmd)
                        {
                            case 0x0A: //Error message
                                serverRecvMsg.GetString();
                                break;
                            case 0x0B: //For your information
                                serverRecvMsg.GetString();
                                break;
                            case 0x14: //MOTD
                                serverRecvMsg.GetString();
                                break;
                            case 0x1E: //Patching exe/dat/spr messages
                            case 0x1F:
                            case 0x20:
                                //DisconnectClient(0x0A, "A new client is avalible, please download it first!");
                                return;
                            case 0x28: //Select other login server
                                selectedLoginServer = random.Next(0, loginServers.Length - 1);
                                break;
                            case 0x64: //character list
                                int nChar = (int)serverRecvMsg.GetByte();
                                charList = new CharacterLoginInfo[nChar];

                                for (int i = 0; i < nChar; i++)
                                {
                                    charList[i].CharName = serverRecvMsg.GetString();
                                    charList[i].WorldName = serverRecvMsg.GetString();
                                    charList[i].WorldIP = serverRecvMsg.PeekUInt32();
                                    serverRecvMsg.AddBytes(localHostBytes);
                                    charList[i].WorldPort = serverRecvMsg.PeekUInt16();
                                    serverRecvMsg.AddUInt16(worldServerPort);
                                }

                                //send this data to client
                                serverRecvMsg.PrepareToSend();
                                clientStream.Write(serverRecvMsg.GetBuffer(), 0, serverRecvMsg.Length);
                                Restart();
                                return;
                            default:
                                break;
                        }
                    }

                    serverRecvMsg.PrepareToSend();
                    clientStream.Write(serverRecvMsg.GetBuffer(), 0, serverRecvMsg.Length);
                    Restart();
                    return;
                }
                else
                    Restart();
            }
            catch (Exception ex)
            {
                Restart();
                WriteDebug(ex.Message + "\nStackTrace: " + ex.StackTrace);
            }
        }

        #endregion

        #region Control

        private void Restart()
        {
            lock (restartLock)
            {
                if (accepting)
                    return;

                Stop();
                StartListenFromClient();
            }
        }

        private void Stop()
        {
            try
            {
                if (connected)
                {
                    connected = false;

                    if (PlayerLogout != null)
                        PlayerLogout.Invoke(this, new EventArgs());
                }

                if (loginClientTcp != null)
                    loginClientTcp.Stop();

                if (clientSocket != null)
                    clientSocket.Close();

                if (clientStream != null)
                    clientStream.Close();

                if (serverTcp != null)
                    serverTcp.Close();
            }
            catch (Exception ex)
            {
                WriteDebug(ex.Message + "\nStackTrace: " + ex.StackTrace);
            }
        }

        #endregion

        #region Send

        private void ServerSend()
        {
            try
            {
                byte[] packet = null;

                lock (serverSendQueueLock)
                {
                    if (serverSendQueue.Count > 0)
                        packet = serverSendQueue.Dequeue();
                    else
                    {
                        serverWriting = false;
                        return;
                    }
                }

                if (packet == null)
                {
                    serverWriting = false;
                    throw new Exception("Null Packet.");
                }

                serverStream.BeginWrite(packet, 0, packet.Length, new AsyncCallback(ServerSendCallBack), null);
            }
            catch (Exception ex)
            {
                WriteDebug(ex.Message + "\nStackTrace: " + ex.StackTrace);
            }
        }

        private void ServerSendCallBack(IAsyncResult ar)
        {
            try
            {
                serverStream.EndWrite(ar);

                bool runAgain = false;

                lock (serverSendQueueLock)
                {
                    if (serverSendQueue.Count > 0)
                        runAgain = true;
                }

                if (runAgain)
                    ServerSend();
                else
                    serverWriting = false;
            }
            catch (ObjectDisposedException) { Restart(); }
            catch (System.IO.IOException) { Restart(); }
            catch (Exception ex)
            {
                Restart();
                WriteDebug(ex.Message + "\nStackTrace: " + ex.StackTrace);
            }
        }

        private void ClientSend()
        {
            try
            {
                byte[] packet = null;

                lock (clientSendQueueLock)
                {
                    if (clientSendQueue.Count > 0)
                        packet = clientSendQueue.Dequeue();
                    else
                    {
                        clientWriting = false;
                        return;
                    }
                }

                if (packet == null)
                {
                    clientWriting = false;
                    throw new Exception("Null Packet.");
                }

                clientStream.BeginWrite(packet, 0, packet.Length, new AsyncCallback(ClientSendCallBack), null);
            }
            catch (System.IO.IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                WriteDebug(ex.Message + "\nStackTrace: " + ex.StackTrace);
            }
        }

        private void ClientSendCallBack(IAsyncResult ar)
        {
            try
            {
                clientStream.EndWrite(ar);

                bool runAgain = false;

                lock (clientSendQueueLock)
                {
                    if (clientSendQueue.Count > 0)
                        runAgain = true;
                }

                if (runAgain)
                    ClientSend();
                else
                    clientWriting = false;
            }
            catch (ObjectDisposedException) { Restart(); }
            catch (System.IO.IOException) { Restart(); }
            catch (Exception ex)
            {
                Restart();
                WriteDebug(ex.Message + "\nStackTrace: " + ex.StackTrace);
            }
        }

        #endregion

        #region Other Functions

        private void WriteDebug(string msg)
        {
            try
            {
                lock (debugLock)
                {
                    System.IO.StreamWriter sw = new System.IO.StreamWriter(System.IO.Path.Combine(Application.StartupPath, "proxy_log.txt"), true);
                    sw.WriteLine(System.DateTime.Now.ToShortDateString() + " " + System.DateTime.Now.ToLongTimeString() + " >> " + msg + "\n");
                    sw.Close();
                }
            }
            catch (Exception)
            {
            }
        }

        private int GetSelectedChar(string name)
        {
            for (int i = 0; i < charList.Length; i++)
            {
                if (charList[i].CharName == name)
                    return i;
            }

            return -1;
        }

        #endregion
    }
}