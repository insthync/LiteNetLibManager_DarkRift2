using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using DarkRift;
using DarkRift.Server;
using DarkRift.Client;

public class DarkRiftTransport : ITransport
{

    private readonly Dictionary<long, IClient> _serverPeers;
    private readonly Queue<TransportEventData> _clientEventQueue;
    private readonly Queue<TransportEventData> _serverEventQueue;

    public DarkRiftClient Client { get; private set; }
    public DarkRiftServer Server { get; private set; }
    public int ServerPeersCount
    {
        get
        {
            if (Server != null)
                return Server.ClientManager.Count;
            return 0;
        }
    }
    public int ServerMaxConnections { get; private set; }
    public bool IsClientStarted
    {
        get { return Client != null && Client.ConnectionState == DarkRift.ConnectionState.Connected; }
    }
    public bool IsServerStarted
    {
        get { return Server != null; }
    }

    public bool HasImplementedPing
    {
        get { return false; }
    }

    public DarkRiftTransport()
    {
        _serverPeers = new Dictionary<long, IClient>();
        _clientEventQueue = new Queue<TransportEventData>();
        _serverEventQueue = new Queue<TransportEventData>();
    }

    public bool ClientReceive(out TransportEventData eventData)
    {
        eventData = default(TransportEventData);
        if (Client == null)
            return false;
        if (_clientEventQueue.Count == 0)
            return false;
        eventData = _clientEventQueue.Dequeue();
        return true;
    }

    public bool ClientSend(byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
    {
        using (DarkRiftWriter drWriter = DarkRiftWriter.Create(writer.Length))
        {
            drWriter.WriteRaw(writer.Data, 0, writer.Length);
            using (Message message = Message.Create(0, drWriter))
            {
                return Client.SendMessage(message, GetSendMode(deliveryMethod));
            }
        }
    }

    public void Destroy()
    {
        StopClient();
        StopServer();
    }

    public bool ServerDisconnect(long connectionId)
    {
        if (IsServerStarted && _serverPeers.ContainsKey(connectionId))
        {
            if (_serverPeers[connectionId].Disconnect())
            {
                _serverPeers.Remove(connectionId);
                return true;
            }
        }
        return false;
    }

    public bool ServerReceive(out TransportEventData eventData)
    {
        eventData = default(TransportEventData);
        if (Server == null)
            return false;
        if (_serverEventQueue.Count == 0)
            return false;
        eventData = _serverEventQueue.Dequeue();
        if (eventData.type == ENetworkEvent.DataEvent && eventData.reader == null)
            return false;
        return true;
    }

    public bool ServerSend(long connectionId, byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
    {
        if (IsServerStarted && _serverPeers.ContainsKey(connectionId) && _serverPeers[connectionId].ConnectionState == DarkRift.ConnectionState.Connected)
        {
            using (DarkRiftWriter drWriter = DarkRiftWriter.Create(writer.Length))
            {
                drWriter.WriteRaw(writer.Data, 0, writer.Length);
                using (Message message = Message.Create(0, drWriter))
                {
                    return _serverPeers[connectionId].SendMessage(message, GetSendMode(deliveryMethod));
                }
            }
        }
        return false;
    }

    public bool StartClient(string address, int port)
    {
        if (IsClientStarted)
            return false;
        _clientEventQueue.Clear();
        Client = new DarkRiftClient();
        Client.Disconnected += Client_Disconnected;
        Client.MessageReceived += Client_MessageReceived;
        if (address.Equals("localhost"))
            address = "127.0.0.1";
        Client.ConnectInBackground(IPAddress.Parse(address), port, IPVersion.IPv4, (exception) =>
        {
            if (exception != null)
            {
                UnityEngine.Debug.LogException(exception);
                _clientEventQueue.Enqueue(new TransportEventData()
                {
                    type = ENetworkEvent.ErrorEvent,
                    socketError = SocketError.ConnectionRefused,
                });
            }
            else
            {
                _clientEventQueue.Enqueue(new TransportEventData()
                {
                    type = ENetworkEvent.ConnectEvent,
                });
            }
        });
        return true;
    }

    private void Client_Disconnected(object sender, DisconnectedEventArgs e)
    {
        _clientEventQueue.Enqueue(GetDisconnectEvent(0, e.LocalDisconnect, e.Error));
    }

    private void Client_MessageReceived(object sender, DarkRift.Client.MessageReceivedEventArgs e)
    {
        // Add receive message to list, will read it later by `ClientReceive` function
        using (Message message = e.GetMessage())
        {
            using (DarkRiftReader reader = message.GetReader())
            {
                _clientEventQueue.Enqueue(new TransportEventData()
                {
                    type = ENetworkEvent.DataEvent,
                    reader = new NetDataReader(reader.ReadRaw(reader.Length)),
                });
            }
        }
    }

    public bool StartServer(int port, int maxConnections)
    {
        if (IsServerStarted)
            return false;
        ServerMaxConnections = maxConnections;
        _serverPeers.Clear();
        _serverEventQueue.Clear();
        Server = new DarkRiftServer(new ServerSpawnData(IPAddress.Any, (ushort)port, IPVersion.IPv4));
        Server.ClientManager.ClientConnected += Server_ClientManager_ClientConnected;
        Server.ClientManager.ClientDisconnected += Server_ClientManager_ClientDisconnected;
        Server.Start();
        return true;
    }

    private void Server_ClientManager_ClientConnected(object sender, ClientConnectedEventArgs e)
    {
        if (ServerPeersCount >= ServerMaxConnections)
        {
            e.Client.Disconnect();
            return;
        }
        e.Client.MessageReceived += Server_ClientManager_Client_MessageReceived;
        _serverPeers[e.Client.ID] = e.Client;
        _serverEventQueue.Enqueue(new TransportEventData()
        {
            type = ENetworkEvent.ConnectEvent,
            connectionId = e.Client.ID,
        });
    }

    private void Server_ClientManager_Client_MessageReceived(object sender, DarkRift.Server.MessageReceivedEventArgs e)
    {
        // Add receive message to list, will read it later by `ServerReceive` function
        using (Message message = e.GetMessage())
        {
            using (DarkRiftReader reader = message.GetReader())
            {
                _serverEventQueue.Enqueue(new TransportEventData()
                {
                    type = ENetworkEvent.DataEvent,
                    connectionId = e.Client.ID,
                    reader = new NetDataReader(reader.ReadRaw(reader.Length)),
                });
            }
        }
    }

    private void Server_ClientManager_ClientDisconnected(object sender, ClientDisconnectedEventArgs e)
    {
        _serverPeers.Remove(e.Client.ID);
        _serverEventQueue.Enqueue(GetDisconnectEvent(e.Client.ID, e.LocalDisconnect, e.Error));
    }

    public void StopClient()
    {
        if (Client != null)
        {
            Client.Disconnect();
            Client.Dispose();
        }
        Client = null;
    }

    public void StopServer()
    {
        if (Server != null)
            Server.Dispose();
        Server = null;
    }

    public TransportEventData GetDisconnectEvent(ushort connectionId, bool localDisconnect, SocketError error)
    {
        TransportEventData result = new TransportEventData()
        {
            type = ENetworkEvent.DisconnectEvent,
            connectionId = connectionId,
        };
        if (localDisconnect)
        {
            result.disconnectInfo = new DisconnectInfo()
            {
                Reason = DisconnectReason.DisconnectPeerCalled,
                SocketErrorCode = error,
            };
        }
        else
        {
            switch (error)
            {
                case SocketError.ConnectionReset:
                    result.disconnectInfo = new DisconnectInfo()
                    {
                        Reason = DisconnectReason.ConnectionFailed,
                        SocketErrorCode = error,
                    };
                    break;
                case SocketError.TimedOut:
                    result.disconnectInfo = new DisconnectInfo()
                    {
                        Reason = DisconnectReason.Timeout,
                        SocketErrorCode = error,
                    };
                    break;
                case SocketError.HostUnreachable:
                    result.disconnectInfo = new DisconnectInfo()
                    {
                        Reason = DisconnectReason.HostUnreachable,
                        SocketErrorCode = error,
                    };
                    break;
                case SocketError.NetworkUnreachable:
                    result.disconnectInfo = new DisconnectInfo()
                    {
                        Reason = DisconnectReason.NetworkUnreachable,
                        SocketErrorCode = error,
                    };
                    break;
                case SocketError.ConnectionAborted:
                    result.disconnectInfo = new DisconnectInfo()
                    {
                        Reason = DisconnectReason.RemoteConnectionClose,
                        SocketErrorCode = error,
                    };
                    break;
                case SocketError.ConnectionRefused:
                    result.disconnectInfo = new DisconnectInfo()
                    {
                        Reason = DisconnectReason.ConnectionRejected,
                        SocketErrorCode = error,
                    };
                    break;
                default:
                    result.disconnectInfo = new DisconnectInfo()
                    {
                        SocketErrorCode = error,
                    };
                    break;
            }
        }
        return result;
    }

    public SendMode GetSendMode(DeliveryMethod deliveryMethod)
    {
        switch (deliveryMethod)
        {
            case DeliveryMethod.ReliableOrdered:
            case DeliveryMethod.ReliableUnordered:
            case DeliveryMethod.ReliableSequenced:
                return SendMode.Reliable;
            case DeliveryMethod.Sequenced:
            default:
                return SendMode.Unreliable;
        }
    }

    public long GetClientRtt()
    {
        throw new System.NotImplementedException();
    }

    public long GetServerRtt(long connectionId)
    {
        throw new System.NotImplementedException();
    }
}
