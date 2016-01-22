﻿using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using m.Http.Backend;
using m.Http.Backend.Tcp;
using m.Http.Metrics;
using m.Logging;
using m.Utils;

namespace m.Http
{
    public class HttpBackend
    {
        protected readonly LoggingProvider.ILogger logger = LoggingProvider.GetLogger(typeof(HttpBackend));

        readonly int port;
        readonly string name;
        readonly int maxKeepAlives;
        readonly int backlog;
        readonly int sessionReadBufferSize;
        readonly TimeSpan sessionReadTimeout;
        readonly TimeSpan sessionWriteTimeout;
        readonly TcpListener listener;
        readonly LifeCycleToken lifeCycleToken;

        readonly RateCounter sessionRate = new RateCounter(100);
        readonly ConcurrentDictionary<long, HttpSession> sessionTable;
        readonly ConcurrentDictionary<long, long> sessionReads;
        readonly ConcurrentDictionary<long, WebSocketSession> webSocketSessionTable; //TODO: track dead reads ?

        readonly WaitableTimer timer;

        long acceptedSessions = 0;
        long acceptedWebSocketSessions = 0;
        int maxConnectedSessions = 0;
        int maxConnectedWebSocketSessions = 0;

        Router router;

        public HttpBackend(IPAddress address,
                           int port,
                           int maxKeepAlives=100,
                           int backlog=128,
                           int sessionReadBufferSize=4096,
                           int sessionReadTimeoutMs=5000,
                           int sessionWriteTimeoutMs=5000)
        {
            this.port = port;
            listener = new TcpListener(address, port);
            this.maxKeepAlives = maxKeepAlives;
            this.backlog = backlog;
            this.sessionReadBufferSize = sessionReadBufferSize;
            sessionReadTimeout = TimeSpan.FromMilliseconds(sessionReadTimeoutMs);
            sessionWriteTimeout = TimeSpan.FromMilliseconds(sessionWriteTimeoutMs);
            lifeCycleToken = new LifeCycleToken();
            sessionTable = new ConcurrentDictionary<long, HttpSession>();
            sessionReads = new ConcurrentDictionary<long, long>();
            webSocketSessionTable = new ConcurrentDictionary<long, WebSocketSession>();

            name = string.Format("{0}({1}:{2})", GetType().Name, address, port);

            timer = new WaitableTimer(name,
                                      TimeSpan.FromSeconds(1),
                                      new [] {
                                          new WaitableTimer.Job("CheckSessionReadTimeouts", CheckSessionReadTimeouts)
                                      });
        }

        public void Start(RouteTable routeTable)
        {
            Start(new Router(routeTable));
        }

        public void Start(Router router)
        {
            if (lifeCycleToken.Start())
            {
                timer.Start();

                this.router = router;
                this.router.Start();

                var connectionLoopThread = new Thread(ConnectionLoop)
                {
                    Priority = ThreadPriority.AboveNormal,
                    IsBackground = false,
                    Name = name
                };

                connectionLoopThread.Start();
            }
        }

        public void Shutdown()
        {
            if (lifeCycleToken.Shutdown())
            {
                timer.Shutdown();
                listener.Stop();
            }
        }

        void ConnectionLoop()
        {
            listener.Start(backlog);
            logger.Info("Listening on {0}", listener.LocalEndpoint);

            while (true)
            {
                try
                {
                    var client = listener.AcceptTcpClient();
                    var sessionId = ++acceptedSessions;

                    Task.Run(() => HandleNewConnection(sessionId, client));
                }
                catch (SocketException e)
                {
                    if (lifeCycleToken.IsShutdown) // triggered by listener.Stop()
                    {
                        logger.Info("Listener shutting down");
                        break;
                    }
                    else
                    {
                        logger.Error("Exception while accepting TcpClient - {0}", e.ToString());
                    }
                }
            }

            logger.Info("Listener closed (accepted: {0})", acceptedSessions);

            router.Shutdown();
        }

        async Task HandleNewConnection(long sessionId, TcpClient client)
        {
            HttpSession newSession;
            try
            {
                newSession = await CreateSession(sessionId, client, maxKeepAlives, sessionReadBufferSize, sessionReadTimeout, sessionWriteTimeout).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logger.Warn("Error creating session - {0}", e);
                client.Close();
                return;
            }

            sessionRate.Count(Time.CurrentTimeMillis, 1);
                
            TrackSession(newSession);

            await HandleSession(newSession).ConfigureAwait(false);
        }

        internal virtual Task<HttpSession> CreateSession(long sessionId,
                                                         TcpClient client,
                                                         int _maxKeepAlives,
                                                         int _sessionReadBufferSize,
                                                         TimeSpan _sessionReadTimeout,
                                                         TimeSpan _sessionWriteTimeout)

        {
            return Task.FromResult(new HttpSession(sessionId, client, client.GetStream(), false, maxKeepAlives, sessionReadBufferSize, sessionReadTimeout, sessionWriteTimeout));
        }

        async Task HandleSession(HttpSession session)
        {
            var closeSessionOnReturn = true;

            try
            {
                var continueSession = true;

                while (continueSession && !session.IsDisconnected())
                {
                    try
                    {
                        TrackSessionRead(session.Id);
                        if (await session.ReadToBufferAsync().ConfigureAwait(false) == 0) // 0 => client clean disconnect
                        {
                            break;
                        }
                    }
                    finally
                    {
                        UntrackSessionRead(session.Id);
                    }

                    int requestBytesParsed, responseBytesWritten;
                    HttpRequest request;
                    while (continueSession && session.TryParseNextRequestFromBuffer(out requestBytesParsed, out request))
                    {
                        Router.HandleResult result = await router.HandleRequest(request, DateTime.UtcNow).ConfigureAwait(false);
                        HttpResponse response = result.HttpResponse;

                        var webSocketUpgradeResponse = response as WebSocketUpgradeResponse;
                        if (webSocketUpgradeResponse == null)
                        {
                            responseBytesWritten = session.WriteResponse(response, request.IsKeepAlive);
                            continueSession = request.IsKeepAlive && session.KeepAlivesRemaining > 0;
                        }
                        else
                        {
                            var isUpgraded = HandleWebsocketUpgrade(session,
                                                                    result.MatchedRouteTableIndex,
                                                                    result.MatchedEndpointIndex,
                                                                    webSocketUpgradeResponse,
                                                                    out responseBytesWritten);
                            continueSession = false;
                            closeSessionOnReturn = !isUpgraded;
                        }

                        if (result.MatchedRouteTableIndex >= 0 && result.MatchedEndpointIndex >= 0)
                        {
                            router.Metrics.CountBytes(result.MatchedRouteTableIndex, result.MatchedEndpointIndex, requestBytesParsed, responseBytesWritten);
                        }
                    }
                }
            }
            catch (RequestException e)
            {
                logger.Warn("Error parsing or bad request - {0}", e.Message);
            }
            catch (SessionStreamException)
            {
                // forced disconnect, socket errors
            }
            catch (Exception e)
            {
                logger.Fatal("Internal server error handling session - {0}", e.ToString());
            }
            finally
            {
                UntrackSession(session.Id);
                if (closeSessionOnReturn)
                {
                    session.CloseQuiety();
                }
            }
        }

        bool HandleWebsocketUpgrade(HttpSession session,
                                    int routeTableIndex,
                                    int endpointIndex,
                                    WebSocketUpgradeResponse response,
                                    out int responseBytesWritten)
        {
            responseBytesWritten = session.WriteWebSocketUpgradeResponse(response);

            var acceptUpgradeResponse = response as WebSocketUpgradeResponse.AcceptUpgradeResponse;
            if (acceptUpgradeResponse == null)
            {
                return false;
            }
            else
            {
                long id = Interlocked.Increment(ref acceptedWebSocketSessions);
                var webSocketSession = new WebSocketSession(id,
                                                            session.TcpClient,
                                                            session.Stream,
                                                            bytesReceived => router.Metrics.CountRequestBytesIn(routeTableIndex, endpointIndex, bytesReceived),
                                                            bytesSent => router.Metrics.CountResponseBytesOut(routeTableIndex, endpointIndex, bytesSent),
                                                            () => UntrackWebSocketSession(id));
                TrackWebSocketSession(webSocketSession);

                try
                {
                    acceptUpgradeResponse.OnAccepted(webSocketSession); //TODO: Task.Run this?
                    return true;
                }
                catch (Exception e)
                {
                    UntrackWebSocketSession(id);
                    logger.Error("Error calling WebSocketUpgradeResponse.OnAccepted callback - {0}", e.ToString());
                    return false;
                }
            }
        }

        void TrackSession(HttpSession session)
        {
            sessionTable[session.Id] = session;
            var sessionCount = sessionTable.Count;

            int currentMax;
            while ((currentMax = maxConnectedSessions) < sessionCount)
            {
                if (Interlocked.CompareExchange(ref maxConnectedSessions, sessionCount, currentMax) != currentMax)
                {
                    continue;
                }
            }
        }

        void UntrackSession(long id)
        {
            HttpSession _;
            sessionTable.TryRemove(id, out _);
        }

        void TrackSessionRead(long id)
        {
            sessionReads[id] = Time.CurrentTimeMillis;
        }

        void UntrackSessionRead(long id)
        {
            long _;
            sessionReads.TryRemove(id, out _);
        }

        void TrackWebSocketSession(WebSocketSession session)
        {
            webSocketSessionTable[session.Id] = session;

            var sessionCount = webSocketSessionTable.Count;
            int currentMax;
            while ((currentMax = maxConnectedWebSocketSessions) < sessionCount)
            {
                if (Interlocked.CompareExchange(ref maxConnectedWebSocketSessions, sessionCount, currentMax) != currentMax)
                {
                    continue;
                }
            }
        }

        void UntrackWebSocketSession(long id)
        {
            WebSocketSession _;
            webSocketSessionTable.TryRemove(id, out _);
        }

        void CheckSessionReadTimeouts()
        {
            var now = Time.CurrentTimeMillis;

            foreach (var kvp in sessionReads)
            {
                if (now - kvp.Value > sessionReadTimeout.TotalMilliseconds)
                {
                    sessionTable[kvp.Key].CloseQuiety();
                }
            }
        }

        public object GetMetricsReport() //TODO: typed report
        {
            if (!lifeCycleToken.IsStarted)
            {
                throw new InvalidOperationException("Not started");
            }

            Thread.MemoryBarrier();

            var now = DateTime.UtcNow;
            return new
            {
                Time = now.ToString(Time.StringFormat),
                TimeHours = now.ToTimeHours(),
                Backend = new {
                    Port = port,
                    Sessions = new {
                        CurrentRate = sessionRate.GetCurrentRate(),
                        MaxRate = sessionRate.MaxRate,
                        Current = sessionTable.Count,
                        Max = maxConnectedSessions,
                        Total = acceptedSessions
                    },
                    WebSocketSessions = new {
                        Current = webSocketSessionTable.Count,
                        Max = maxConnectedWebSocketSessions,
                        Total = acceptedWebSocketSessions
                    }
                },
                HostReports = HostReport.Generate(router, router.Metrics)
            };
        }
    }
}
