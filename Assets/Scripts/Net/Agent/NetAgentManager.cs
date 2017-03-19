﻿using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

[Serializable] public class ServerDataReceivedEvent : UnityEvent<NetServerAtom, NetQosType> { }
[Serializable] public class ClientDataReceivedEvent : UnityEvent<NetClientAtom, NetAgent, NetQosType> { }

public class NetAgentManager : MonoBehaviour 
{
    public string MyPort = "8888";
    public string ConnectIp = "localhost";
    public string ConnectPort = "8889";

    [SerializeField] private bool _isHost;
    [SerializeField] private bool _isRunning;

    public bool IsHost { get { return Me != null && Me.IsHost; } }
    public bool IsRunning { get { return _isRunning; } }

    public List<NetAgent> Clients = new List<NetAgent>();
    public NetAgent Host;
    public NetAgent Me;

    public ServerDataReceivedEvent ServerDataReceivedEvent;
    public ClientDataReceivedEvent ClientDataReceivedEvent;

    public void StartHost()
    {
        if(!SL.Get<NetConnectionManager>().IsOpen)
        {
            _isHost = true;
            _isRunning = true;

            // TODO: Get the various channels our streams wish to support, so they can be injected.
            var channels = new NetQosType[] { NetQosType.Reliable, NetQosType.Unreliable };

            SL.Get<NetConnectionManager>().Open(Convert.ToInt32(MyPort), channels);

            // Set up a local client for this host.
            // TODO: My god this constructor is ugly.  Need to refactor this to not have to pass in some
            // junk null value.
            Host = Me = new NetAgent(null, isHost: true, isLocal: true);
            Clients.Add(Me);
        }
        else
        {
            Log.Debug(this, "Disconnect before trying to re-connect.");
        }
    }

    public void StartClient()
    {
        if (!SL.Get<NetConnectionManager>().IsOpen)
        {
            _isHost = false;

            // TODO: Get the various channels our streams wish to support, so they can be injected.
            var channels = new NetQosType[] { NetQosType.Reliable, NetQosType.Unreliable };
            
            SL.Get<NetConnectionManager>().Open(Convert.ToInt32(MyPort), channels);
            SL.Get<NetConnectionManager>().Connect(ConnectIp, Convert.ToInt32(ConnectPort));

            Me = new NetAgent(null, isHost: false, isLocal: true);
        }
        else
        {
            Log.Debug(this, "Disconnect before trying to re-connect.");
        }
    }

    public void Disconnect()
    {
        _isRunning = false;
        if(SL.Get<NetConnectionManager>().IsOpen)
        {
            foreach(var connection in SL.Get<NetConnectionManager>().Connections)
            {
                SL.Get<NetConnectionManager>().Disconnect(connection);
            }

            SL.Get<NetConnectionManager>().Close();

            Host = null;
            Me = null;
            Clients.Clear();
        }
        else
        {
            Debug.Log("No connection open.");
        }
    }

    /// <summary>
    /// Send message from the host to a client.
    /// </summary>
    public void HostSend(NetServerAtom atom, NetAgent agent, NetQosType channelType)
    {
        if(agent != null && IsHost)
        {
            // If the client is local, skip the whole networking thing.
            if(agent.IsLocal)
            {
                processAtom(atom, channelType, agent);
            }
            else
            {
                SL.Get<NetConnectionManager>().Send(agent.Connection, channelType, atom);
            }
        }
    }

    /// <summary>
    /// Send message from a client to the host.
    /// </summary>
    public void ClientSend(NetClientAtom atom, NetQosType channelType)
    {
        if(!IsHost && Host != null)
        {
            // If the host is local, skip the whole networking thing.
            if(Host == Me)
            {
                processAtom(atom, channelType, Me);
            }
            else
            {
                SL.Get<NetConnectionManager>().Send(Host.Connection, channelType, atom);
            }
        }
    }

    public void MigrateHost()
    {
        // TODO:
    }

    private void Start()
    {
        SL.Get<NetConnectionManager>().Init(new BinarySerializer());
        SL.Get<NetConnectionManager>().ConnectedEvent.AddListener(onConnected);
        SL.Get<NetConnectionManager>().ConnectedFailedEvent.AddListener(onConnectionFailed);
        SL.Get<NetConnectionManager>().DiconnectedEvent.AddListener(onDisconnected);
        SL.Get<NetConnectionManager>().AtomReceivedEvent.AddListener(onAtomReceived);
    }

    private void onDisconnected(NetConnection connection)
    {
        // EARLY OUT! //
        if(connection != null) return;

        // EARLY OUT! //
        if(!_isRunning) return;

        if(IsHost)
        {
            var index = Clients.FindIndex(c => c.Connection == connection);
            if(index != -1)
            {
                Clients.RemoveAt(index);
            }
            else
            {
                Log.Debug(this, "Got a disconnect from a client that wasn't registered. " + connection);
            }
        }
        else
        {
            if(Host != null)
            {
                Host = null;
            }
            else
            {
                Log.Debug(this, "Got a connection to a second host? " + connection);
            }
        }
    }

    private void onConnectionFailed(string arg0)
    {
        // Do something?
    }

    private void onConnected(NetConnection connection)
    {
        Log.Debug(this, "connected: " + connection.ConnectionId);
        _isRunning = true;

        if(IsHost)
        {
            Clients.Add(new NetAgent(connection, isHost: false, isLocal: false));
        }
        else
        {
            if(Host == null)
            {
                Host = new NetAgent(connection, isHost: true, isLocal: false);
            }
            else
            {
                Log.Debug(this, "Got a connection to a second host? " + connection);
            }
        }
    }

    private void onAtomReceived(NetAtom atom, NetQosType channelType, NetConnection sender)
    {
        var agent = getAgent(sender);
        processAtom(atom, channelType, agent);
    }

    private void processAtom(NetAtom atom, NetQosType channelType, NetAgent agent)
    {
        if(agent != null)
        {
            bool isFromServer;
            if(agent.IsHost && agent.IsLocal)
            {
                // We need to handle the special case of the local client being the host as well.
                // In that case we have to use the net atom to determine the type of data we received.
                // It makes sense to trust the packet in that case, since it never passed through the
                // network layer.
                isFromServer = atom is NetServerAtom;
            }
            else
            {
                isFromServer = agent.IsHost;
            }


            if(isFromServer)
            {
                var serverAtom = atom as NetServerAtom;
                if(serverAtom != null)
                {
                    ServerDataReceivedEvent.Invoke(serverAtom, channelType);
                }
                else
                {
                    Log.Warning(this, "Atom was sent by server but is not a server atom: " + atom);
                }
            }
            else
            {
                var clientAtom = atom as NetClientAtom;
                if(clientAtom != null)
                {
                    ClientDataReceivedEvent.Invoke(clientAtom, agent, channelType);
                }
                else
                {
                    Log.Warning(this, "Atom was sent by client but is not a client atom: " + atom);
                }
            }
        }
    }

    /// <summary>
    /// Gets the agent with the given connection (the local client will have a null connection).
    /// The agent is either the host, one of the remote clients, the local client, or no match (unexpected).
    /// </summary>
    private NetAgent getAgent(NetConnection connection)
    {
        
        NetAgent agent = null;
        if(connection == null)
        {
            agent = Me;
        }
        else
        {
            int id = connection.ConnectionId;
            if(Host != null && Host.Connection != null && Host.Connection.ConnectionId == id)
            {
                agent = Host;
            }
            else
            {
                int index = Clients.FindIndex(p => p.Connection != null && p.Connection.ConnectionId == id);
                if (index != -1)
                {
                    agent = Clients[index];
                }
                else
                {
                    Log.Warning(this, "Looking for a sender with no agent: " + connection);
                }
            }
        }

        return agent;
    }
}