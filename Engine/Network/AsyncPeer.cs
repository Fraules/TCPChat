﻿using Engine.API;
using Engine.Helpers;
using Engine.Model.Client;
using Engine.Network.Connections;
using Lidgren.Network;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Threading;

namespace Engine.Network
{
  public enum PeerState
  {
    NotConnected = 0,
    ConnectedToService = 1,
    ConnectedToPeers = 2,
  }

  public class AsyncPeer :
    MarshalByRefObject,
    IDisposable
  {
    #region nested types
    private class WaitingCommandContainer
    {
      public IPackage Package { get; private set; }
      public byte[] RawData { get; private set; }
      public bool Unreliable { get; private set; }

      public WaitingCommandContainer(IPackage package, byte[] rawData, bool unreliable)
      {
        Package = package;
        RawData = rawData;
        Unreliable = unreliable;
      }
    }
    #endregion

    #region consts
    public const string NetConfigString = "Peer TCPChat";
    public const int ConnectionTimeOut = 30 * 1000;
    private const int KeySize = 256;
    #endregion

    #region private fields
    [SecurityCritical] private readonly object syncObject = new object();
    [SecurityCritical] private Dictionary<string, List<WaitingCommandContainer>> waitingCommands;
    [SecurityCritical] private Dictionary<string, Packer> packers;
    [SecurityCritical] private NetConnection serviceConnection;
    [SecurityCritical] private NetPeer handler;
    [SecurityCritical] private int state; //PeerState
    [SecurityCritical] private bool disposed;
    [SecurityCritical] private SynchronizationContext syncContext;
    [SecurityCritical] private ClientRequestQueue requestQueue;
    [SecurityCritical] private ECDiffieHellmanCng diffieHellman;
    #endregion

    #region events and properties
    /// <summary>
    /// Состояние пира.
    /// </summary>
    public PeerState State
    {
      [SecuritySafeCritical]
      get { return (PeerState)state; }
    }
    #endregion

    #region constructor
    [SecurityCritical]
    internal AsyncPeer()
    {
      waitingCommands = new Dictionary<string, List<WaitingCommandContainer>>();
      packers = new Dictionary<string, Packer>();
      syncContext = new EngineSyncContext();
      requestQueue = new ClientRequestQueue();

      diffieHellman = new ECDiffieHellmanCng(KeySize);
      diffieHellman.KeyDerivationFunction = ECDiffieHellmanKeyDerivationFunction.Hash;
      diffieHellman.HashAlgorithm = CngAlgorithm.Sha256;
    }
    #endregion

    #region public methods
    /// <summary>
    /// Подключение к P2P сервису. Для создания UDP окна.
    /// </summary>
    /// <param name="remotePoint">Адрес сервиса.</param>
    [SecurityCritical]
    internal void ConnectToService(IPEndPoint remotePoint)
    {
      ThrowIfDisposed();

      if (Interlocked.CompareExchange(ref state, (int)PeerState.ConnectedToService, (int)PeerState.NotConnected) != (int)PeerState.NotConnected)
        throw new InvalidOperationException("Peer has not right state.");

      if (handler != null && handler.Status == NetPeerStatus.Running)
        throw new ArgumentException("Already runned.");

      var config = new NetPeerConfiguration(NetConfigString);
      config.Port = 0;
      config.AcceptIncomingConnections = true;
      config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);

      if (remotePoint.AddressFamily == AddressFamily.InterNetworkV6)
        config.LocalAddress = IPAddress.IPv6Any;

      handler = new NetPeer(config);
      syncContext.Send(RegisterReceived, handler);
      handler.Start();

      var hail = handler.CreateMessage();
      using (var client = ClientModel.Get())
      {
        var localPoint = new IPEndPoint(Connection.GetIpAddress(remotePoint.AddressFamily), handler.Port);

        hail.Write(client.User.Nick);
        hail.Write(localPoint);
      }

      serviceConnection = handler.Connect(remotePoint, hail);

      ClientModel.Logger.WriteDebug("AsyncPeer.ConnectToService({0})", remotePoint);
    }

    [SecurityCritical]
    private void RegisterReceived(object obj)
    {
      var server = (NetPeer)obj;
      server.RegisterReceivedCallback(OnReceive);
    }

    /// <summary>
    /// Ожидать подключение другого клиента.
    /// <remarks>Возможно вызвать только после подключения к сервису.</remarks>
    /// </summary>
    /// <param name="waitingPoint">Конечная точка, от которой следует ждать подключение.</param>
    [SecurityCritical]
    internal void WaitConnection(IPEndPoint waitingPoint)
    {
      ThrowIfDisposed();

      int oldState = Interlocked.CompareExchange(ref state, (int)PeerState.ConnectedToPeers, (int)PeerState.ConnectedToService);
      if (oldState == (int)PeerState.NotConnected)
        throw new InvalidOperationException("Peer has not right state.");

      // Создания и отправка сообщения для пробивания NAT,
      // и возможности принять входящее соединение
      var holePunchMessage = handler.CreateMessage();
      holePunchMessage.Write((byte)0);
      handler.SendUnconnectedMessage(holePunchMessage, waitingPoint);

      DisconnectFromService();

      ClientModel.Logger.WriteDebug("AsyncPeer.WaitConnection({0})", waitingPoint);
    }

    /// <summary>
    /// Подключение к другому клиенту.
    /// <remarks>Возможно вызвать только после подключения к сервису.</remarks>
    /// </summary>
    /// <param name="peerId">Id пира к которому подключаемся.</param>
    /// <param name="remotePoint">Адрес клиента</param>
    [SecurityCritical]
    internal void ConnectToPeer(string peerId, IPEndPoint remotePoint)
    {
      ThrowIfDisposed();

      int oldState = Interlocked.CompareExchange(ref state, (int)PeerState.ConnectedToPeers, (int)PeerState.ConnectedToService);
      if (oldState == (int)PeerState.NotConnected)
        throw new InvalidOperationException("Peer has not right state.");

      if (handler == null)
        throw new InvalidOperationException("Handler not created.");

      handler.Connect(remotePoint, CreateHailMessage());
      
      DisconnectFromService();

      ClientModel.Logger.WriteDebug("AsyncPeer.ConnectToPeer({0}, {1})", peerId, remotePoint);
    }

    /// <summary>
    /// Имеется ли соедиенение к данному пиру.
    /// </summary>
    /// <param name="peerId">Id соединения.</param>
    [SecuritySafeCritical]
    public bool IsConnected(string peerId)
    {
      ThrowIfDisposed();

      if (handler == null || handler.Status != NetPeerStatus.Running)
        return false;

      return FindConnection(peerId) != null;
    }

    #region Send message
    /// <summary>
    /// Отправляет команду. <b>Если соединения нет, то вначале установит его.</b>
    /// </summary>
    /// <typeparam name="T">Тип параметра пакета.</typeparam>
    /// <param name="peerId">Идентификатор соединения, которому отправляется команда.</param>
    /// <param name="id">Индетификатор команды.</param>
    /// <param name="content">Параметр пакета.</param>
    /// <param name="unreliable">Отправить ненадежное сообщение. (быстрее)</param>
    [SecuritySafeCritical]
    public void SendMessage<T>(string peerId, long id, T content, bool unreliable = false)
    {
      SendMessage(peerId, new Package<T>(id, content), null, unreliable);
    }

    /// <summary>
    /// Отправляет команду. <b>Если соединения нет, то вначале установит его.</b>
    /// </summary>
    /// <typeparam name="T">Тип параметра пакета.</typeparam>
    /// <param name="peerId">Идентификатор соединения, которому отправляется команда.</param>
    /// <param name="id">Индетификатор команды.</param>
    /// <param name="content">Параметр пакета.</param>
    /// <param name="rawData">Данные не требующие сериализации.</param>
    /// <param name="unreliable">Отправить ненадежное сообщение. (быстрее)</param>
    [SecuritySafeCritical]
    public void SendMessage<T>(string peerId, long id, T content, byte[] rawData, bool unreliable = false)
    {
      SendMessage(peerId, new Package<T>(id, content), rawData, unreliable);
    }

    /// <summary>
    /// Отправляет команду. <b>Если соединения нет, то вначале установит его.</b>
    /// </summary>
    /// <param name="peerId">Идентификатор соединения, которому отправляется команда.</param>
    /// <param name="id">Индетификатор команды.</param>
    /// <param name="unreliable">Отправить ненадежное сообщение. (быстрее)</param>
    [SecuritySafeCritical]
    public void SendMessage(string peerId, long id, bool unreliable = false)
    {
      SendMessage(peerId, new Package(id), null, unreliable);
    }

    /// <summary>
    /// Отправляет команду. <b>Если соединения нет, то вначале установит его.</b>
    /// </summary>
    /// <param name="peerId">Идентификатор соединения, которому отправляется команда.</param>
    /// <param name="package">Индетификатор пакета.</param>
    /// <param name="rawData">Данные не требующие серализации.</param>
    /// <param name="unreliable">Отправить ненадежное сообщение. (быстрее)</param>
    [SecuritySafeCritical]
    public void SendMessage(string peerId, IPackage package, byte[] rawData, bool unreliable = false)
    {
      ThrowIfDisposed();

      if (handler == null || handler.Status != NetPeerStatus.Running)
      {
        SaveCommandAndConnect(peerId, package, rawData, unreliable);
        return;
      }

      var connection = FindConnection(peerId);
      if (connection == null)
      {
        SaveCommandAndConnect(peerId, package, rawData, unreliable);
        return;
      }

      var deliveryMethod = unreliable ? NetDeliveryMethod.Unreliable : NetDeliveryMethod.ReliableOrdered;
      var message = CreateMessage(peerId, package, rawData);
      handler.SendMessage(message, connection, deliveryMethod, 0);
    }
    #endregion

    #region Send message if connected
    /// <summary>
    /// Отправляет команду. <b>Если соединения нет, то команда отправлена не будет.</b>
    /// </summary>
    /// <typeparam name="T">Тип параметра пакета.</typeparam>
    /// <param name="peerId">Идентификатор соединения, которому отправляется команда.</param>
    /// <param name="id">Индетификатор команды.</param>
    /// <param name="content">Параметр команды.</param>
    /// <param name="unreliable">Отправить ненадежное сообщение. (быстрее)</param>
    /// <returns>Отправлено ли сообщение.</returns>
    [SecuritySafeCritical]
    public bool SendMessageIfConnected<T>(string peerId, long id, T content, bool unreliable = false)
    {
      return SendMessageIfConnected(peerId, new Package<T>(id, content), null, unreliable);
    }

    /// <summary>
    /// Отправляет команду. <b>Если соединения нет, то команда отправлена не будет.</b>
    /// </summary>
    /// <param name="peerId">Идентификатор соединения, которому отправляется команда.</param>
    /// <param name="package">Индетификатор пакета.</param>
    /// <param name="rawData">Данные не требующие сериализации.</param>
    /// <param name="unreliable">Отправить ненадежное сообщение. (быстрее)</param>
    /// <returns>Отправлено ли сообщение.</returns>
    [SecuritySafeCritical]
    public bool SendMessageIfConnected(string peerId, IPackage package, byte[] rawData, bool unreliable = false)
    {
      ThrowIfDisposed();

      if (handler == null || handler.Status != NetPeerStatus.Running)
        return false;

      var connection = FindConnection(peerId);
      if (connection == null)
        return false;

      var deliveryMethod = unreliable ? NetDeliveryMethod.Unreliable : NetDeliveryMethod.ReliableOrdered;
      var message = CreateMessage(peerId, package, rawData);
      handler.SendMessage(message, connection, deliveryMethod, 0);
      return true;
    }
    #endregion
    #endregion

    #region private methods
    [SecurityCritical]
    private Packer GetPacker(string peerId)
    {
      Packer packer;
      lock (syncObject)
        packers.TryGetValue(peerId, out packer);

      if (packer == null)
        throw new InvalidOperationException(string.Format("Packer not set, for connection {0}", peerId));

      return packer;
    }

    [SecurityCritical]
    private NetOutgoingMessage CreateMessage(string peerId, IPackage package, byte[] rawData)
    {
      var packer = GetPacker(peerId);
      var packed = packer.Pack(package, rawData);
      
      var message = handler.CreateMessage(packed.Length);
      message.Write(packed.Data, 0, packed.Length);
      return message;
    }

    [SecurityCritical]
    private NetOutgoingMessage CreateHailMessage()
    {
      string nick;
      using (var client = ClientModel.Get())
        nick = client.User.Nick;

      var publicKeyBlob = diffieHellman.PublicKey.ToByteArray();
      var hailMessage = handler.CreateMessage();
      hailMessage.Write(nick);
      hailMessage.Write(publicKeyBlob.Length);
      hailMessage.Write(publicKeyBlob);

      return hailMessage;
    }

    [SecurityCritical]
    private void SaveCommandAndConnect(string peerId, IPackage package, byte[] rawData, bool unreliable)
    {
      lock (syncObject)
      {
        List<WaitingCommandContainer> commands;

        if (!waitingCommands.TryGetValue(peerId, out commands))
        {
          commands = new List<WaitingCommandContainer>();
          waitingCommands.Add(peerId, commands);
        }

        commands.Add(new WaitingCommandContainer(package, rawData, unreliable));
      }

      ClientModel.Api.ConnectToPeer(peerId);
    }

    [SecurityCritical]
    private void DisconnectFromService()
    {
      if (serviceConnection != null)
      {
        serviceConnection.Disconnect(string.Empty);
        serviceConnection = null;
      }
    }

    [SecurityCritical]
    private NetConnection FindConnection(string id)
    {
      return handler.Connections.SingleOrDefault(new Finder(id).Equals);
    }

    [SecurityCritical]
    private List<NetConnection> FindConnections(IEnumerable<string> ids)
    {
      return handler.Connections.Where(new Finder(ids).Contains).ToList();
    }

    private class Finder
    {
      private string id;
      private IEnumerable<string> ids;

      public Finder(string id)
      {
        this.id = id;
      }

      public Finder(IEnumerable<string> ids)
      {
        this.ids = ids;
      }

      [SecurityCritical]
      public bool Equals(NetConnection connection)
      {
        return string.Equals((string)connection.Tag, id);
      }

      [SecurityCritical]
      public bool Contains(NetConnection connection)
      {
        return ids.Contains((string)connection.Tag);
      }
    }
    #endregion

    #region callback method
    [SecurityCritical]
    private void OnReceive(object obj)
    {
      if (handler == null || handler.Status != NetPeerStatus.Running)
        return;

      NetIncomingMessage message;

      while ((message = handler.ReadMessage()) != null)
      {
        switch (message.MessageType)
        {
          case NetIncomingMessageType.ErrorMessage:
          case NetIncomingMessageType.WarningMessage:
            ClientModel.Notifier.AsyncError(new AsyncErrorEventArgs { Error = new NetException(message.ReadString()) });
            break;

          case NetIncomingMessageType.ConnectionApproval:
            OnApprove(message);
            break;

          case NetIncomingMessageType.StatusChanged:
            NetConnectionStatus status = (NetConnectionStatus)message.ReadByte();

            if (status == NetConnectionStatus.Connected && state == (int)PeerState.ConnectedToPeers)
              OnPeerConnected(message);

            if (status == NetConnectionStatus.Connected && state == (int)PeerState.ConnectedToService)
              OnServiceConnected(message);

            if (status == NetConnectionStatus.Disconnecting || status == NetConnectionStatus.Disconnected)
              OnDisconnected(message);
            break;

          case NetIncomingMessageType.Data:
          case NetIncomingMessageType.UnconnectedData:
            if (state == (int)PeerState.ConnectedToPeers)
              OnPackageReceived(message);
            break;
        }
      }
    }

    [SecurityCritical]
    private void OnApprove(NetIncomingMessage message)
    {
      message.SenderConnection.Approve(CreateHailMessage());
      ClientModel.Logger.WriteDebug("AsyncPeer.Approve()");
    }

    [SecurityCritical]
    private void OnServiceConnected(NetIncomingMessage message)
    {
      ClientModel.Logger.WriteDebug("AsyncPeer.ServiceConnect()");
    }

    [SecurityCritical]
    private void OnPeerConnected(NetIncomingMessage message)
    {
      var hailMessage = message.SenderConnection.RemoteHailMessage;
      if (hailMessage == null)
      {
        message.SenderConnection.Deny();
        ClientModel.Logger.WriteWarning("ConnectionId is null [Message: {0}, SenderEndPoint: {1}]", message.ToString(), message.SenderEndPoint);
        return;
      }

      var connectionId = hailMessage.ReadString();
      var publicKeySize = hailMessage.ReadInt32();
      var publicKeyBlob = hailMessage.ReadBytes(publicKeySize);
      var publicKey = CngKey.Import(publicKeyBlob, CngKeyBlobFormat.EccPublicBlob);
      var key = diffieHellman.DeriveKeyMaterial(publicKey);

      message.SenderConnection.Tag = connectionId;

      lock (syncObject)
      {
        // Add connection packer
        var packer = new Packer();
        packer.SetKey(key);
        packers.Add(connectionId, packer);

        // Invoke waiting commands
        List<WaitingCommandContainer> commands;
        if (waitingCommands.TryGetValue(connectionId, out commands))
        {
          foreach (var command in commands)
            SendMessage(connectionId, command.Package, command.RawData, command.Unreliable);

          waitingCommands.Remove(connectionId);
        }
      }

      ClientModel.Logger.WriteDebug("AsyncPeer.PeerConnected({0})", connectionId);
    }

    [SecurityCritical]
    private void OnDisconnected(NetIncomingMessage message)
    {
      var connectionId = (string) message.SenderConnection.Tag;
      message.SenderConnection.Tag = null;

      if (connectionId != null)
        packers.Remove(connectionId);
    }

    [SecurityCritical]
    private void OnPackageReceived(NetIncomingMessage message)
    {
      try
      {
        var peerId = (string)message.SenderConnection.Tag;
        var packer = GetPacker(peerId);
        var unpacked = packer.Unpack<IPackage>(message.Data);

        var command = ClientModel.Api.GetCommand(unpacked.Package.Id);
        var args = new ClientCommandArgs(peerId, unpacked);
        
        requestQueue.Add(peerId, command, args);
      }
      catch (Exception exc)
      {
        ClientModel.Notifier.AsyncError(new AsyncErrorEventArgs { Error = exc });
        ClientModel.Logger.Write(exc);
      }
    }
    #endregion

    #region IDisposable
    [SecurityCritical]
    private void ThrowIfDisposed()
    {
      if (disposed)
        throw new ObjectDisposedException("Object disposed");
    }

    [SecuritySafeCritical]
    public void Dispose()
    {
      if (disposed)
        return;

      disposed = true;

      if (requestQueue != null)
        requestQueue.Dispose();

      if (handler != null)
        handler.Shutdown(string.Empty);

      if (diffieHellman != null)
        diffieHellman.Dispose();

      lock (syncObject)
      {
        waitingCommands.Clear();
        packers.Clear();
      }
    }
    #endregion
  }
}
