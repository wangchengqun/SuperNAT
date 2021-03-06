﻿using SuperNAT.AsyncSocket;
using SuperNAT.Bll;
using SuperNAT.Common;
using SuperNAT.Core;
using SuperNAT.Model;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SuperNAT.Core
{
    public class HttpServer : AppServer<HttpSession, HttpRequestInfo>
    {
        public ConcurrentQueue<Request> RequestQueue = new ConcurrentQueue<Request>();
        public HttpServer(ServerOption serverOption) : base(new HttpReceiveFilter())
        {
            InitOption(serverOption);
            OnConnected += Connected;
            OnReceived += Received;
            OnClosed += Closed;
            WriteRequest();
        }

        private void Connected(HttpSession session)
        {
            LogHelper.Info($"HTTP客户端【{session.SessionId},{session.RemoteEndPoint}】已连接【{session.LocalEndPoint}】", false);
        }

        private async void ForwardProxy(HttpSession session, HttpModel httpModel, Map map)
        {
            try
            {
                using HttpRequestMessage httpRequest = new HttpRequestMessage()
                {
                    Method = new HttpMethod(httpModel.Method),
                    RequestUri = new Uri($"{map.protocol}://{map.local_endpoint}{httpModel.Path}")
                };
                LogHelper.Info($"{map.name} {httpModel.Method} {httpRequest.RequestUri.AbsoluteUri} {httpModel.Headers.ToJson()}");
                if (httpRequest.Method != HttpMethod.Get && httpModel.Content?.Length > 0)
                {
                    var body = httpModel.Content;
                    var bodyStr = body.ToUTF8String();
                    //记录请求小于1kb的参数
                    if (httpModel.Content.Length < 1024)
                    {
                        LogHelper.Info($"{map.name} {httpModel.Method} {httpRequest.RequestUri.AbsoluteUri} {bodyStr}");
                    }
                    httpRequest.Content = new StringContent(bodyStr, Encoding.UTF8, httpModel.ContentType.Split(";")[0]);
                }
                using HttpClient _httpClient = new HttpClient();
                //替换Host 不然400 Bad Request
                //httpModel.Headers["Host"] = map.local_endpoint;
                foreach (var item in httpModel.Headers)
                {
                    if (!item.Key.EqualsWhithNoCase("Content-Type"))
                    {
                        if (!httpRequest.Content?.Headers.TryAddWithoutValidation(item.Key, item.Value) ?? true)
                        {
                            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(item.Key, item.Value);
                        }
                    }
                }
                if (map.protocol == "https")
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                }
                var response = await _httpClient.SendAsync(httpRequest);
                //回传给浏览器
                var respHttpModel = new HttpModel
                {
                    HttpVersion = $"{map.protocol.ToUpper()}/{response.Version.ToString()}",
                    StatusCode = (int)response.StatusCode,
                    StatusMessage = response.StatusCode.ToString(),
                    Local = map.local_endpoint,
                    Headers = response.Headers.ToDictionary(),
                    ResponseTime = DateTime.Now
                };
                foreach (var item in response.Content.Headers)
                {
                    if (item.Key.EqualsWhithNoCase("Content-Type"))
                    {
                        respHttpModel.ContentType = string.Join(";", item.Value);
                    }
                    else
                    {
                        if (item.Key.EqualsWhithNoCase("Content-Length"))
                            continue;
                        respHttpModel.Headers.Add(item.Key, string.Join(";", item.Value));
                    }
                }
                respHttpModel.Headers.Remove("Transfer-Encoding");//response收到的是完整的 这个响应头要去掉 不然浏览器解析出错
                respHttpModel.Content = DataHelper.StreamToBytes(response.Content.ReadAsStreamAsync().Result);

                HttpResponse httpResponse = new HttpResponse()
                {
                    HttpVersion = respHttpModel.HttpVersion,
                    Headers = respHttpModel.Headers,
                    Status = respHttpModel.StatusCode,
                    StatusMessage = respHttpModel.StatusMessage
                };
                if (respHttpModel.Content?.Length > 0)
                {
                    httpResponse.ContentType = respHttpModel.ContentType;
                    httpResponse.Body = respHttpModel.Content;
                }
                //把处理信息返回到客户端
                session.Send(httpResponse.Write());

                var timeSpan = (DateTime.Now - httpModel.RequestTime);
                var totalSize = (httpResponse.Body?.Length ?? 0) * 1.00 / 1024;
                LogHelper.Info($"{map.user_name} {map.client_name} {map?.name} {httpModel.Method} {httpModel.Path} {respHttpModel.StatusCode} {respHttpModel.StatusMessage} {Math.Round(totalSize, 1)}KB {timeSpan.TotalMilliseconds}ms");

            }
            catch (Exception ex)
            {
                LogHelper.Error($"【{session.LocalEndPoint}】请求地址：{map.protocol}://{httpModel.Host}{httpModel.Path}，正向代理异常：{ex}");
                //把处理信息返回到客户端
                session.Write("server error");
            }
        }

        private void Received(HttpSession session, HttpRequestInfo requestInfo)
        {
            Task.Run(() =>
            {
                try
                {
                    if (!requestInfo.Success)
                    {
                        LogHelper.Error($"http请求解析异常，ip地址：{session.RemoteEndPoint}");
                        session.Write("request parse error");
                        return;
                    }
                    session.RequestInfo = requestInfo;
                    var httpModel = new HttpModel
                    {
                        RequestTime = DateTime.Now,
                        ServerId = ServerId,
                        SessionId = session.SessionId,
                        HttpVersion = requestInfo.HttpVersion,
                        Method = requestInfo.Method,
                        Path = requestInfo.Path,
                        Headers = requestInfo.Headers,
                        Host = requestInfo.BaseUrl,
                        ContentType = requestInfo.ContentType,
                        Content = requestInfo.Body
                    };
                    var map = ServerManager.MapList.Find(c => c.remote_endpoint == httpModel.Host || (c.remote == httpModel.Host && c.remote_port == 80));
                    if (map == null)
                    {
                        LogHelper.Error($"映射不存在，请求：{httpModel.Host}{httpModel.Path} {httpModel.Headers.ToJson()} {httpModel.Content.ToUTF8String()}");
                        //把处理信息返回到客户端
                        session.Write("map not found");
                        return;
                    }
                    //正向代理www.supernat.cn
                    if (map.proxy_type == (int)proxy_type.正向代理)
                    {
                        ForwardProxy(session, httpModel, map);
                        return;
                    }
                    //转发请求
                    var natSession = ServerManager.NATServer.GetSingleSession(c => c.MapList.Any(c => c.remote_endpoint == httpModel.Host || (c.remote == httpModel.Host && c.remote_port == 80)));
                    if (natSession == null)
                    {
                        LogHelper.Error($"穿透客户端未连接到服务器，请求地址：{httpModel.Host}{httpModel.Path}");
                        //把处理信息返回到客户端
                        session.Write("nat client not found");
                    }
                    else
                    {
                        //压缩Body
                        httpModel.Content = requestInfo.ContentLength > 0 ? DataHelper.Compress(requestInfo.Body) : null;
                        //转发数据
                        var pack = new JsonData()
                        {
                            Type = (int)JsonType.HTTP,
                            Action = (int)HttpAction.Request,
                            Data = httpModel.ToJson()
                        };
                        natSession.Send(PackHelper.CreatePack(pack));
                        session.NatSession = natSession;
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.Error($"【{session.LocalEndPoint}】请求地址：{requestInfo.BaseUrl}{requestInfo.Path}，处理发生异常：{ex}");
                    //把处理信息返回到客户端
                    session.Write("server error");
                }
            });
        }

        private void Closed(HttpSession session)
        {
            LogHelper.Debug($"HTTP客户端【{session.SessionId},{session.RemoteEndPoint}】已下线", false);
        }

        public void ProcessData(NatSession session, NatRequestInfo requestInfo, HttpModel httpModel)
        {
            try
            {
                switch (requestInfo.Body.Action)
                {
                    case (int)HttpAction.Response:
                        {
                            var context = GetSingleSession(c => c.SessionId == httpModel.SessionId);
                            if (context == null)
                            {
                                LogHelper.Error($"未找到上下文context，SessionId={httpModel.SessionId}");
                                return;
                            }
                            HttpResponse httpResponse = new HttpResponse()
                            {
                                HttpVersion = httpModel.HttpVersion,
                                Headers = httpModel.Headers,
                                Status = httpModel.StatusCode,
                                StatusMessage = httpModel.StatusMessage
                            };
                            if (httpModel.Content?.Length > 0)
                            {
                                //解压
                                var byteData = DataHelper.Decompress(httpModel.Content);
                                httpResponse.ContentType = httpModel.ContentType;
                                httpResponse.Body = byteData;
                            }
                            //把处理信息返回到客户端
                            context.Send(httpResponse.Write());
                            var response_time = DateTime.Now;
                            var timeSpan = (response_time - httpModel.RequestTime);
                            var totalSize = (httpResponse.Body?.Length ?? 0) * 1.00 / 1024;
                            var map = session.MapList.Find(c => c.remote_endpoint == httpModel.Host);
                            LogHelper.Info($"{session.Client.user_name} {session.Client.name} {map?.name} {httpModel.Method} {httpModel.Path} {httpModel.StatusCode} {httpModel.StatusMessage} {Math.Round(totalSize, 1)}KB {timeSpan.TotalMilliseconds}ms");

                            var request = new Request
                            {
                                request_url = $"{map.protocol}://{map.remote_endpoint}{httpModel.Path}",
                                request_method = httpModel.Method,
                                client_ip = session.RemoteEndPoint.ToString(),
                                user_id = session.Client.user_id,
                                request_time = httpModel.RequestTime,
                                response_time = response_time,
                                handle_time = Convert.ToInt64(timeSpan.TotalMilliseconds),//ms
                                create_time = DateTime.Now,
                                requet_content = context.RequestInfo.Body == null ? null : Encoding.UTF8.GetString(context.RequestInfo.Body),
                                response_content = httpResponse.Body.Length <= 1024 ? Encoding.UTF8.GetString(httpResponse.Body) : "",
                                status_code = httpModel.StatusCode,
                                status_message = httpModel.StatusMessage,
                                total_size = httpResponse.Body?.Length ?? 0,
                                speed = Math.Round(totalSize / (timeSpan.TotalMilliseconds / 1000), 2),//KB/s
                                map_id = map.id
                            };
                            lock (RequestQueue)
                                RequestQueue.Enqueue(request);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                LogHelper.Error($"HttpsServer ProcessData穿透处理异常，{ex}");
            }
        }

        public void WriteRequest()
        {
            Task.Run(() =>
            {
                while (true)
                {
                    try
                    {
                        List<Request> requestList = new List<Request>();
                        while (requestList.Count <= 10 && RequestQueue.TryDequeue(out Request request))
                        {
                            requestList.Add(request);
                        }
                        if (requestList.Any())
                        {
                            new RequestBll().AddList(requestList);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogHelper.Error($"写请求失败：{ex}");
                    }
                    Thread.Sleep(10000);
                }
            });
        }
    }
}