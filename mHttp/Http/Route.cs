﻿using System;
using System.Threading.Tasks;

using m.Http.Handlers;
using m.Http.Routing;

namespace m.Http
{
    public static class Route
    {
        public static EndpointBuilder Get(string route)
        {
            return new EndpointBuilder(Method.GET, new Routing.Route(route));
        }

        public static EndpointBuilder Post(string route)
        {
            return new EndpointBuilder(Method.POST, new Routing.Route(route));
        }

        public static EndpointBuilder Put(string route)
        {
            return new EndpointBuilder(Method.PUT, new Routing.Route(route));
        }

        public static EndpointBuilder Delete(string route)
        {
            return new EndpointBuilder(Method.DELETE, new Routing.Route(route));
        }

        public static RateLimitedEndpoint LimitRate(this Endpoint ep, int requestsPerSecond, int burstRequestsPerSecond=0)
        {
            if (burstRequestsPerSecond == 0)
            {
                burstRequestsPerSecond = requestsPerSecond;
            }

            return new RateLimitedEndpoint(ep.Method, ep.Route, ep.Handler, requestsPerSecond, burstRequestsPerSecond);
        }

        public static Endpoint WithAction(this EndpointBuilder b, Action a)
        {
            return new Endpoint(b.Method, b.Route, Handler.FromAction(a));
        }

        public static Endpoint WithAsyncAction(this EndpointBuilder b, Func<Task> a)
        {
            return new Endpoint(b.Method, b.Route, Handler.FromAsyncAction(a));
        }

        public static Endpoint With(this EndpointBuilder b, Func<HttpResponse> f)
        {
            return new Endpoint(b.Method, b.Route, Handler.From(f));
        }

        public static Endpoint WithAsync(this EndpointBuilder b, Func<Task<HttpResponse>> f)
        {
            return new Endpoint(b.Method, b.Route, Handler.FromAsync(f));
        }

        public static Endpoint WithAction(this EndpointBuilder b, Action<IHttpRequest> a)
        {
            return new Endpoint(b.Method, b.Route, Handler.FromAction(a));
        }

        public static Endpoint WithAsyncAction(this EndpointBuilder b, Func<IHttpRequest, Task> a)
        {
            return new Endpoint(b.Method, b.Route, Handler.FromAsyncAction(a));
        }

        public static Endpoint With(this EndpointBuilder b, Func<IHttpRequest, HttpResponse> f)
        {
            return new Endpoint(b.Method, b.Route, Handler.From(f));
        }

        public static Endpoint WithAsync(this EndpointBuilder b, Func<IHttpRequest, Task<HttpResponse>> f)
        {
            return new Endpoint(b.Method, b.Route, f);
        }

        #region Websocket
        public static Endpoint With(this EndpointBuilder b, Func<IWebSocketUpgradeRequest, WebSocketUpgradeResponse> f)
        {
            return new Endpoint(b.Method, b.Route, Handler.From(f));
        }
        #endregion

        #region Json
        public static Endpoint WithAsync<TReq>(this EndpointBuilder b, Func<JsonRequest<TReq>, Task<HttpResponse>> f)
        {
            return new Endpoint(b.Method, b.Route, JsonHandler<TReq>.FromAsync(f));
        }
        #endregion
    }
}
