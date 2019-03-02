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
    public DarkRiftClient client { get; private set; }
    public DarkRiftServer server { get; private set; }
    private readonly Dictionary<long, IClient> serverPeers;
    private readonly Queue<TransportEventData> clientEventQueue;
    private readonly Queue<TransportEventData> serverEventQueue;

    public DarkRiftTransport()
    {
        serverPeers = new Dictionary<long, IClient>();
        clientEventQueue = new Queue<TransportEventData>();
        serverEventQueue = new Queue<TransportEventData>();
    }

    public bool ClientReceive(out TransportEventData eventData)
    {
        eventData = default(TransportEventData);
        if (client == null)
            return false;
        if (clientEventQueue.Count == 0)
            return false;
        eventData = clientEventQueue.Dequeue();
        return true;
    }

    public bool ClientSend(DeliveryMethod deliveryMethod, NetDataWriter writer)
    {
        using (DarkRiftWriter drWriter = DarkRiftWriter.Create(writer.Length))
        {
            drWriter.WriteRaw(writer.Data, 0, writer.Length);
            using (Message message = Message.Create(0, drWriter))
            {
                return client.SendMessage(message, GetSendMode(deliveryMethod));
            }
        }
    }

    public void Destroy()
    {
        StopClient();
        StopServer();
    }

    public int GetFreePort()
    {
        Socket socketV4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socketV4.Bind(new IPEndPoint(IPAddress.Any, 0));
        int port = ((IPEndPoint)socketV4.LocalEndPoint).Port;
        socketV4.Close();
        return port;
    }

    public bool IsClientStarted()
    {
        return client != null && client.ConnectionState == DarkRift.ConnectionState.Connected;
    }

    public bool IsServerStarted()
    {
        return server != null;
    }

    public bool ServerDisconnect(long connectionId)
    {
        if (IsServerStarted() && serverPeers.ContainsKey(connectionId))
            return serverPeers[connectionId].Disconnect();
        return false;
    }

    public bool ServerReceive(out TransportEventData eventData)
    {
        eventData = default(TransportEventData);
        if (server == null)
            return false;
        if (serverEventQueue.Count == 0)
            return false;
        eventData = serverEventQueue.Dequeue();
        return true;
    }

    public bool ServerSend(long connectionId, DeliveryMethod deliveryMethod, NetDataWriter writer)
    {
        if (IsServerStarted() && serverPeers.ContainsKey(connectionId))
        {
            using (DarkRiftWriter drWriter = DarkRiftWriter.Create(writer.Length))
            {
                drWriter.WriteRaw(writer.Data, 0, writer.Length);
                using (Message message = Message.Create(0, drWriter))
                {
                    return serverPeers[connectionId].SendMessage(message, GetSendMode(deliveryMethod));
                }
            }
        }
        return false;
    }

    public bool StartClient(string connectKey, string address, int port)
    {
        clientEventQueue.Clear();
        client = new DarkRiftClient();
        client.Disconnected += Client_Disconnected;
        client.MessageReceived += Client_MessageReceived;
        client.Connect(IPAddress.Parse(address), port, IPVersion.IPv4);
        clientEventQueue.Enqueue(new TransportEventData()
        {
            type = ENetworkEvent.ConnectEvent,
        });
        return true;
    }

    private void Client_Disconnected(object sender, DisconnectedEventArgs e)
    {
        clientEventQueue.Enqueue(new TransportEventData()
        {
            type = ENetworkEvent.DisconnectEvent,
        });
    }

    private void Client_MessageReceived(object sender, DarkRift.Client.MessageReceivedEventArgs e)
    {
        // Add receive message to list, will read it later by `ClientReceive` function
        using (Message message = e.GetMessage())
        {
            using (DarkRiftReader reader = message.GetReader())
            {
                clientEventQueue.Enqueue(new TransportEventData()
                {
                    type = ENetworkEvent.DataEvent,
                    reader = new NetDataReader(reader.ReadRaw(reader.Length)),
                });
            }
        }
    }

    public bool StartServer(string connectKey, int port, int maxConnections)
    {
        serverPeers.Clear();
        serverEventQueue.Clear();
        server = new DarkRiftServer(new ServerSpawnData(IPAddress.Parse("127.0.0.1"), (ushort)port, IPVersion.IPv4));
        server.ClientManager.ClientConnected += Server_ClientManager_ClientConnected;
        server.ClientManager.ClientDisconnected += Server_ClientManager_ClientDisconnected;
        return true;
    }

    private void Server_ClientManager_ClientConnected(object sender, ClientConnectedEventArgs e)
    {
        e.Client.MessageReceived += Server_ClientManager_Client_MessageReceived;
        serverPeers[e.Client.ID] = e.Client;
        serverEventQueue.Enqueue(new TransportEventData()
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
                serverEventQueue.Enqueue(new TransportEventData()
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
        serverPeers.Remove(e.Client.ID);
        serverEventQueue.Enqueue(new TransportEventData()
        {
            type = ENetworkEvent.DisconnectEvent,
            connectionId = e.Client.ID,
        });
    }

    public void StopClient()
    {
        client.Disconnect();
        client.Dispose();
        client = null;
    }

    public void StopServer()
    {
        server.Dispose();
        server = null;
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
}
