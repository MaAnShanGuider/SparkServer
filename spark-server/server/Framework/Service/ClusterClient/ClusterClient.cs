﻿using NetSprotoType;
using Newtonsoft.Json.Linq;
using SparkServer.Framework.MessageQueue;
using SparkServer.Framework.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SparkServer.Framework.Service.ClusterClient
{
    class WaitForSendRequest
    {
        public int Source { get; set; }
        public int Session { get; set; }
        public string method { get; set; }
        public ClusterClientRequest request { get; set; }
    }

    class WaitForResponseRequest
    {
        public int Source { get; set; }
        public int Session { get; set; }
    }

    class ClusterClient : ServiceBase
    {
        private Dictionary<string, long> m_node2conn = new Dictionary<string, long>();
        private Dictionary<string, Queue<WaitForSendRequest>> m_waitForSendRequests = new Dictionary<string, Queue<WaitForSendRequest>>();
        private Dictionary<int, RPCResponseCallback> m_remoteResponseCallbacks = new Dictionary<int, RPCResponseCallback>();
        private Dictionary<long, Dictionary<int, WaitForResponseRequest>> m_conn2sessions = new Dictionary<long, Dictionary<int, WaitForResponseRequest>>();
        private int m_totalRemoteSession = 0;

        Dictionary<string, Method> m_socketMethods = new Dictionary<string, Method>();
        private int m_tcpObjectId = 0;

        private SkynetPacketManager m_skynetPacketManager = new SkynetPacketManager();

        private JObject m_clusterConfig = new JObject();

        public override void Init()
        {
            base.Init();

            RegisterSocketMethods("SocketConnected", SocketConnected);
            RegisterSocketMethods("SocketError", SocketError);
            RegisterSocketMethods("SocketData", SocketData);

            RegisterServiceMethods("Request", Request);
        }

        public void ParseClusterConfig(string clusterPath)
        {
            string clusterConfigText = ConfigHelper.LoadFromFile(clusterPath);
            m_clusterConfig = JObject.Parse(clusterConfigText);
        }

        public void SetTCPObjectId(int tcpObjectId)
        {
            m_tcpObjectId = tcpObjectId;
        }

        protected override void OnSocket(Message msg)
        {
            base.OnSocket(msg);

            Method method = null;
            bool isExist = m_socketMethods.TryGetValue(msg.Method, out method);
            if (isExist)
            {
                method(msg.Source, msg.RPCSession, msg.Method, msg.Data);
            }
            else
            {
                LoggerHelper.Info(m_serviceId, string.Format("ClusterClient unknow socket method:{0}", msg.Method));
            }
        }

        private void SocketConnected(int source, int session, string method, byte[] param)
        {
            ClusterClientSocketConnected socketConnected = new ClusterClientSocketConnected(param);
            string ipEndpoint = string.Format("{0}:{1}", socketConnected.ip, socketConnected.port);

            long tempConnection = 0;
            bool isExist = m_node2conn.TryGetValue(ipEndpoint, out tempConnection);
            if (isExist)
            {
                m_node2conn.Remove(ipEndpoint);
            }
            m_node2conn.Add(ipEndpoint, socketConnected.connection);

            Queue<WaitForSendRequest> waitQueue = null;
            isExist = m_waitForSendRequests.TryGetValue(ipEndpoint, out waitQueue);
            if (isExist)
            {
                int count = waitQueue.Count;
                for (int i = 0; i < count; i ++)
                {
                    WaitForSendRequest request = waitQueue.Dequeue();
                    RemoteRequest(request.Source, request.method, request.request, socketConnected.connection, request.Session);
                }
                m_waitForSendRequests.Remove(ipEndpoint);
            }
        }

        private void SocketError(int source, int session, string method, byte[] param)
        {
            NetSprotoType.SocketError error = new NetSprotoType.SocketError(param);

            string node = "";
            foreach(var pair in m_node2conn)
            {
                if (pair.Value == error.connection)
                {
                    node = pair.Key;
                    break;
                }
            }

            bool canFind = node != "";
            // if connection already exist, that means queue of waitForRequest is empty, because 
            // it will send and clear after connect success
            if (canFind)
            {
                Dictionary<int, WaitForResponseRequest> waitForResponseRequests = null;
                bool isExist = m_conn2sessions.TryGetValue(error.connection, out waitForResponseRequests);
                if (isExist)
                {
                    Queue<int> tempRemoteSessions = new Queue<int>();
                    foreach(var pair in waitForResponseRequests)
                    {
                        tempRemoteSessions.Enqueue(pair.Key);
                    }

                    int count = tempRemoteSessions.Count;
                    for (int i = 0; i < count; i ++)
                    {
                        int remoteSession = tempRemoteSessions.Dequeue();
                        ProcessRemoteResponse(remoteSession, null, RPCError.SocketDisconnected);
                    }
                    m_conn2sessions.Remove(error.connection);
                }
            }
            else
            {
                string ipEndpoint = error.errorText;
                Queue<WaitForSendRequest> waitQueue = null;
                bool isExist = m_waitForSendRequests.TryGetValue(ipEndpoint, out waitQueue);
                if (isExist)
                {
                    int count = waitQueue.Count;
                    for (int i = 0; i < count; i++)
                    {
                        WaitForSendRequest req = waitQueue.Dequeue();
                        DoError(req.Source, req.Session, RPCError.SocketDisconnected, string.Format("RemoteCall {0} failure", req.method));
                    }

                    m_waitForSendRequests.Remove(ipEndpoint);
                }
            }
        }

        private void SocketData(int source, int session, string method, byte[] param)
        {
            SkynetClusterResponse response = m_skynetPacketManager.UnpackSkynetResponse(param);
            if (response == null)
            {
                return;
            }

            int tag = NetProtocol.GetInstance().GetTag("RPC");
            RPCParam rpcParam = new RPCParam(response.Data);

            int remoteSession = response.Session;
            ProcessRemoteResponse(remoteSession, Encoding.ASCII.GetBytes(rpcParam.param), response.ErrorCode);
        }

        private void ProcessRemoteResponse(int remoteSession, byte[] param, RPCError errorCode)
        {
            RPCResponseCallback responseCallback = null;
            bool isExist = m_remoteResponseCallbacks.TryGetValue(remoteSession, out responseCallback);
            if (isExist)
            {
                responseCallback.Callback(responseCallback.Context, "RemoteResponseCallback", param, errorCode);
                m_remoteResponseCallbacks.Remove(remoteSession);
            }
            else
            {
                LoggerHelper.Info(m_serviceId, string.Format("ClusterServer SocketData unknow remoteSession:{0}", remoteSession));
            }
        }

        private void Request(int source, int session, string method, byte[] param)
        {
            NetSprotoType.ClusterClientRequest request = new NetSprotoType.ClusterClientRequest(param);
            string remoteNode = request.remoteNode;
            long connectionId = 0;

            bool isExist = m_node2conn.TryGetValue(remoteNode, out connectionId);
            if (isExist)
            {
                RemoteRequest(source, method, request, connectionId, session);
            }
            else
            {
                CacheRequest(source, session, method, request, remoteNode);
            }
        }

        private void CacheRequest(int source, int session, string method, ClusterClientRequest request, string remoteNode)
        {
            string ipEndpoint = m_clusterConfig[remoteNode].ToString();
            Queue<WaitForSendRequest> waittingQueue = null;
            bool isExist = m_waitForSendRequests.TryGetValue(ipEndpoint, out waittingQueue);
            if (!isExist)
            {
                waittingQueue = new Queue<WaitForSendRequest>();
                m_waitForSendRequests.Add(ipEndpoint, waittingQueue);
            }

            if (waittingQueue.Count <= 0)
            {
                string[] ipResult = ipEndpoint.Split(':');
                string remoteIp = ipResult[0];
                int remotePort = Int32.Parse(ipResult[1]);

                ConnectMessage connectMessage = new ConnectMessage();
                connectMessage.IP = remoteIp;
                connectMessage.Port = remotePort;
                connectMessage.TcpObjectId = m_tcpObjectId;
                connectMessage.Type = SocketMessageType.Connect;

                NetworkPacketQueue.GetInstance().Push(connectMessage);
            }

            WaitForSendRequest waitRequest = new WaitForSendRequest();
            waitRequest.Source = source;
            waitRequest.Session = session;
            waitRequest.method = method;
            waitRequest.request = request;

            waittingQueue.Enqueue(waitRequest);
        }

        private void RemoteRequest(int source, string method, NetSprotoType.ClusterClientRequest request, long connectionId, int session)
        {
            int tag = NetProtocol.GetInstance().GetTag("RPC");
            RPCParam rpcParam = new RPCParam();
            rpcParam.method = method;
            rpcParam.param = request.param;

            int remoteSession = ++m_totalRemoteSession;
            List<byte[]> buffers = m_skynetPacketManager.PackSkynetRequest(request.remoteService, remoteSession, tag, rpcParam.encode());

            RPCContext rpcContext = new RPCContext();
            rpcContext.LongDict["ConnectionId"] = connectionId;
            rpcContext.IntegerDict["RemoteSession"] = remoteSession;
            rpcContext.IntegerDict["SourceSession"] = session;
            rpcContext.IntegerDict["Source"] = source;
            rpcContext.StringDict["Method"] = method;

            RPCResponseCallback rpcResponseCallback = new RPCResponseCallback();
            rpcResponseCallback.Callback = RemoteResponseCallback;
            rpcResponseCallback.Context = rpcContext;
            m_remoteResponseCallbacks.Add(remoteSession, rpcResponseCallback);

            Dictionary<int, WaitForResponseRequest> waitResponseDict = null;
            bool isExist = m_conn2sessions.TryGetValue(connectionId, out waitResponseDict);
            if (!isExist)
            {
                waitResponseDict = new Dictionary<int, WaitForResponseRequest>();
                m_conn2sessions.Add(connectionId, waitResponseDict);
            }

            WaitForResponseRequest waitForResponseRequest = new WaitForResponseRequest();
            waitForResponseRequest.Session = session;
            waitForResponseRequest.Source = source;
            waitResponseDict.Add(remoteSession, waitForResponseRequest);

            NetworkPacket networkPacket = new NetworkPacket();
            networkPacket.ConnectionId = connectionId;
            networkPacket.TcpObjectId = m_tcpObjectId;
            networkPacket.Buffers = buffers;
            networkPacket.Type = SocketMessageType.DATA;

            NetworkPacketQueue.GetInstance().Push(networkPacket);
        }

        private void RemoteResponseCallback(RPCContext context, string method, byte[] param, RPCError error)
        {
            long connectionId = context.LongDict["ConnectionId"];
            int remoteSession = context.IntegerDict["RemoteSession"];
            int sourceSession = context.IntegerDict["SourceSession"];
            int source = context.IntegerDict["Source"];
            string sourceMethod = context.StringDict["Method"];

            if (error == RPCError.OK)
            {
                DoResponse(source, sourceMethod, param, sourceSession);
            }
            else
            {
                DoError(source, sourceSession, error, "RemoteCall Error");
            }

            Dictionary<int, WaitForResponseRequest> waitForResponseDict = null;
            bool isExist = m_conn2sessions.TryGetValue(connectionId, out waitForResponseDict);
            if (isExist)
            {
                waitForResponseDict.Remove(remoteSession);
            }
        }

        private void RegisterSocketMethods(string methodName, Method method)
        {
            m_socketMethods.Add(methodName, method);
        }
    }
}