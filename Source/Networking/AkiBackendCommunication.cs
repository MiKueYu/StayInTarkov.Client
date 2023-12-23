﻿using BepInEx.Logging;
using Comfort.Common;
using EFT;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StayInTarkov.Configuration;
using StayInTarkov.Coop;
using StayInTarkov.Coop.Matchmaker;
using StayInTarkov.Coop.Networking;
using StayInTarkov.Coop.NetworkPacket;
using StayInTarkov.ThirdParty;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR;

namespace StayInTarkov.Networking
{
    public class AkiBackendCommunication : IDisposable
    {
        public const int DEFAULT_TIMEOUT_MS = 5000;
        public const int DEFAULT_TIMEOUT_LONG_MS = 9999;
        public const string PACKET_TAG_METHOD = "m";
        public const string PACKET_TAG_SERVERID = "serverId";
        public const string PACKET_TAG_DATA = "data";

        private string m_Session;

        public string Session
        {
            get
            {
                return m_Session;
            }
            set { m_Session = value; }
        }



        private string m_RemoteEndPoint;

        public string RemoteEndPoint
        {
            get
            {
                if (string.IsNullOrEmpty(m_RemoteEndPoint))
                    m_RemoteEndPoint = StayInTarkovHelperConstants.GetBackendUrl();

                return m_RemoteEndPoint;

            }
            set { m_RemoteEndPoint = value; }
        }

        //public bool isUnity;
        private Dictionary<string, string> m_RequestHeaders { get; set; }

        private static AkiBackendCommunication m_Instance { get; set; }
        public static AkiBackendCommunication Instance
        {
            get
            {
                if (m_Instance == null || m_Instance.Session == null || m_Instance.RemoteEndPoint == null)
                    m_Instance = new AkiBackendCommunication();

                return m_Instance;
            }
        }

        public HttpClient HttpClient { get; set; }

        protected ManualLogSource Logger;

        //WebSocketSharp.WebSocket WebSocket { get; set; }

        public static int PING_LIMIT_HIGH { get; } = 125;
        public static int PING_LIMIT_MID { get; } = 100;


        protected AkiBackendCommunication(ManualLogSource logger = null)
        {
            // disable SSL encryption
            ServicePointManager.Expect100Continue = true;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };

            if (logger != null)
                Logger = logger;
            else
                Logger = BepInEx.Logging.Logger.CreateLogSource("Request");

            if (string.IsNullOrEmpty(RemoteEndPoint))
                RemoteEndPoint = StayInTarkovHelperConstants.GetBackendUrl();

            GetHeaders();
            ConnectToAkiBackend();
            PeriodicallySendPing();
            PeriodicallySendPooledData();

            HttpClient = new HttpClient();
            foreach (var item in GetHeaders())
            {
                HttpClient.DefaultRequestHeaders.Add(item.Key, item.Value);
            }
            HttpClient.MaxResponseContentBufferSize = long.MaxValue;
            HttpClient.Timeout = new TimeSpan(0, 0, 0, 0, 1000);

            HighPingMode = PluginConfigSettings.Instance.CoopSettings.ForceHighPingMode;

        }

        private void ConnectToAkiBackend()
        {
            PooledJsonToPostToUrl.Add(new KeyValuePair<string, string>("/coop/connect", "{}"));
        }

        private Profile MyProfile { get; set; }

        //private HashSet<string> WebSocketPreviousReceived { get; set; }

        public void WebSocketCreate(Profile profile)
        {
            MyProfile = profile;

            Logger.LogDebug("WebSocketCreate");
            //if (WebSocket != null && WebSocket.ReadyState != WebSocketSharp.WebSocketState.Closed)
            //{
            //    Logger.LogDebug("WebSocketCreate:WebSocket already exits");
            //    return;
            //}

            Logger.LogDebug("Request Instance is connecting to WebSocket");

            var webSocketPort = PluginConfigSettings.Instance.CoopSettings.SITWebSocketPort;
            var wsUrl = $"{StayInTarkovHelperConstants.GetREALWSURL()}:{webSocketPort}/{profile.ProfileId}?";
            Logger.LogDebug(webSocketPort);
            Logger.LogDebug(StayInTarkovHelperConstants.GetREALWSURL());
            Logger.LogDebug(wsUrl);

            //WebSocketPreviousReceived = new HashSet<string>();
            //WebSocket = new WebSocketSharp.WebSocket(wsUrl);
            //WebSocket.WaitTime = TimeSpan.FromMinutes(1);
            //WebSocket.EmitOnPing = true;
            //WebSocket.Connect();
            //WebSocket.Send("CONNECTED FROM SIT COOP");
            //// ---
            //// Start up after initial Send
            //WebSocket.OnError += WebSocket_OnError;
            //WebSocket.OnMessage += WebSocket_OnMessage;
            // ---

            _sitWebSocket = new SITWebSocket(this);
            _sitWebSocket.WebSocketCreate(profile);
        }

        SITWebSocket _sitWebSocket { get; set; }


        public void WebSocketClose()
        {
            if (_sitWebSocket != null)
            {
                Logger.LogDebug("WebSocketClose");
                _sitWebSocket.WebSocketClose();
                _sitWebSocket = null;
            }
        }

        public async void PostDownWebSocketImmediately(Dictionary<string, object> packet)
        {
            await Task.Run(() =>
            {
                //if (WebSocket != null)
                //    WebSocket.Send(packet.SITToJson());
                if(_sitWebSocket != null)
                    _sitWebSocket.Send(packet.SITToJson());
            });
        }

        public async void PostDownWebSocketImmediately(string packet)
        {
            await Task.Run(() =>
            {
                if (_sitWebSocket != null)
                    _sitWebSocket.Send(packet);
            });
        }

        //private void WebSocket_OnError(object sender, WebSocketSharp.ErrorEventArgs e)
        //{
        //    Logger.LogError($"{nameof(WebSocket_OnError)}: {e.Message} {Environment.NewLine}");
        //    Logger.LogError($"{nameof(WebSocket_OnError)}: {e.Exception}");
        //    WebSocket_OnError();
        //    WebSocketClose();
        //    WebSocketCreate(MyProfile);
        //}

        //private void WebSocket_OnError()
        //{
        //    Logger.LogError($"Your PC has failed to connect and send data to the WebSocket with the port {PluginConfigSettings.Instance.CoopSettings.SITWebSocketPort} on the Server {StayInTarkovHelperConstants.GetBackendUrl()}! Application will now close.");
        //    if (Singleton<ISITGame>.Instantiated)
        //    {
        //        Singleton<ISITGame>.Instance.Stop(Singleton<GameWorld>.Instance.MainPlayer.ProfileId, Singleton<ISITGame>.Instance.MyExitStatus, Singleton<ISITGame>.Instance.MyExitLocation);
        //    }
        //    else
        //        Application.Quit();
        //}

        private void WebSocket_OnMessage(object sender, WebSocketSharp.MessageEventArgs e)
        {
            if (e == null)
                return;

            if (string.IsNullOrEmpty(e.Data))
                return;

            //ProcessPacketBytes(e.RawData);

        }

        private void ProcessPacketBytes(byte[] data)
        {
            try
            {
                if (data == null)
                    return;

                if (data.Length == 0)
                    return;

                Dictionary<string, object> packet = null;
               
                // Use StreamReader & JsonTextReader to improve memory / cpu usage
                //using (var streamReader = new StreamReader(new MemoryStream(data)))
                //{
                //    using (var reader = new JsonTextReader(streamReader))
                //    {
                //        var serializer = new JsonSerializer();
                //        packet = serializer.Deserialize<Dictionary<string, object>>(reader);
                //    }
                //}

                var coopGameComponent = CoopGameComponent.GetCoopGameComponent();

                if (coopGameComponent == null)
                    return;

                if (packet == null)
                    return;

                if (DEBUGPACKETS)
                {
                    Logger.LogInfo(packet.SITToJson());
                }

                if (packet.ContainsKey("dataList"))
                {
                    if (ProcessDataListPacket(ref packet))
                        return;
                }

                //Logger.LogDebug($"Step.1. Packet exists. {packet.ToJson()}");

                // If this is a pong packet, resolve and create a smooth ping
                if (ProcessPong(ref packet, ref coopGameComponent))
                    return;

                if (packet.ContainsKey("HostPing"))
                {
                    var dtHP = new DateTime(long.Parse(packet["HostPing"].ToString()));
                    var timeSpanOfHostToMe = DateTime.UtcNow - dtHP;
                    HostPing = (int)Math.Round(timeSpanOfHostToMe.TotalMilliseconds);
                    return;
                }

                // Receiving a Player Extracted packet. Process into ExtractedPlayers List
                if (packet.ContainsKey("Extracted"))
                {
                    if (Singleton<ISITGame>.Instantiated && !Singleton<ISITGame>.Instance.ExtractedPlayers.Contains(packet["profileId"].ToString()))
                    {
                        Singleton<ISITGame>.Instance.ExtractedPlayers.Add(packet["profileId"].ToString());
                    }
                    return;
                }

                // If this is an endSession packet, end the session for the clients
                if (packet.ContainsKey("endSession") && MatchmakerAcceptPatches.IsClient)
                {
                    Logger.LogDebug("Received EndSession from Server. Ending Game.");
                    if (coopGameComponent.LocalGameInstance == null)
                        return;

                    coopGameComponent.ServerHasStopped = true;
                    return;
                }



                // -------------------------------------------------------
                // Add to the Coop Game Component Action Packets
                if (coopGameComponent == null || coopGameComponent.ActionPackets == null || coopGameComponent.ActionPacketHandler == null)
                    return;

                //ProcessSITPacket(ref packet);

                //if (packet.ContainsKey(PACKET_TAG_METHOD)
                //    && packet[PACKET_TAG_METHOD].ToString() == "Move")
                //    coopGameComponent.ActionPacketHandler.ActionPacketsMovement.TryAdd(packet);
                //else if (packet.ContainsKey(PACKET_TAG_METHOD)
                //    && packet[PACKET_TAG_METHOD].ToString() == "ApplyDamageInfo")
                //{
                //    coopGameComponent.ActionPacketHandler.ActionPacketsDamage.TryAdd(packet);
                //}
                //else
                //    coopGameComponent.ActionPacketHandler.ActionPackets.TryAdd(packet);

            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }

        private bool ProcessPong(ref Dictionary<string, object> packet, ref CoopGameComponent coopGameComponent)
        {
            if (packet.ContainsKey("pong"))
            {
                var pongRaw = long.Parse(packet["pong"].ToString());
                var dtPong = new DateTime(pongRaw);
                var serverPing = (int)(DateTime.UtcNow - dtPong).TotalMilliseconds;
                if (coopGameComponent.ServerPingSmooth.Count > 60)
                    coopGameComponent.ServerPingSmooth.TryDequeue(out _);
                coopGameComponent.ServerPingSmooth.Enqueue(serverPing);
                coopGameComponent.ServerPing = coopGameComponent.ServerPingSmooth.Count > 0 ? (int)Math.Round(coopGameComponent.ServerPingSmooth.Average()) : 1;
                return true;
            }

            return false;
        }

        private void ProcessSITPacket(ref Dictionary<string, object> packet)
        {
            // If this is a SIT serialization packet
            if (packet.ContainsKey(PACKET_TAG_DATA) && packet.ContainsKey(PACKET_TAG_METHOD))
            {
                var data = packet[PACKET_TAG_DATA];
                if (data == null)
                    return;

                          
                if (!packet.ContainsKey("profileId"))
                {
                    //Logger.LogInfo(nameof(ProcessSITPacket));
                    //Logger.LogInfo("No profileId found");
                    var bpp = new BasePlayerPacket();
                    bpp = bpp.DeserializePacketSIT(data.ToString());
                    if (!string.IsNullOrEmpty(bpp.ProfileId))
                        packet.Add("profileId", bpp.ProfileId);

                    bpp = null;
                    //Logger.LogInfo(packet.ToJson());
                }
            }
        }

        private bool ProcessDataListPacket(ref Dictionary<string, object> packet)
        {
            var coopGC = CoopGameComponent.GetCoopGameComponent();
            if (coopGC == null)
                return false;

            if (!packet.ContainsKey("dataList"))
                return false;

            JArray dataList = JArray.FromObject(packet["dataList"]);
            
            foreach (var d in dataList)
            {
                // This needs to be a little more dynamic but for now. This switch will do.
                // Depending on the method defined, deserialize packet to defined type
                switch (packet[PACKET_TAG_METHOD].ToString())
                {
                    case "PlayerStates":
                        PlayerStatePacket playerStatePacket = new PlayerStatePacket();
                        playerStatePacket = (PlayerStatePacket)playerStatePacket.Deserialize((byte[])d);
                        if(coopGC.Players.ContainsKey(playerStatePacket.ProfileId))
                            coopGC.Players[playerStatePacket.ProfileId].ReceivePlayerStatePacket(playerStatePacket);

                        break;
                }
               
            }

            return true;
        }

        public static AkiBackendCommunication GetRequestInstance(bool createInstance = false, ManualLogSource logger = null)
        {
            if (createInstance)
            {
                return new AkiBackendCommunication(logger);
            }

            return Instance;
        }

        public static bool DEBUGPACKETS { get; } = false;

        public bool HighPingMode { get; set; }
        public BlockingCollection<byte[]> PooledBytesToPost { get; } = new();
        public BlockingCollection<KeyValuePair<string, Dictionary<string, object>>> PooledDictionariesToPost { get; } = new();
        public BlockingCollection<List<Dictionary<string, object>>> PooledDictionaryCollectionToPost { get; } = new();

        public BlockingCollection<KeyValuePair<string, string>> PooledJsonToPostToUrl { get; } = new();

        public void SendDataToPool(string url, string serializedData)
        {
            PooledJsonToPostToUrl.Add(new(url, serializedData));
        }

        public void SendDataToPool(string serializedData)
        {
            SendDataToPool(Encoding.UTF8.GetBytes(serializedData));
        }

        public void SendDataToPool(byte[] serializedData)
        {
            if (HighPingMode)
            {
                if(_sitWebSocket != null)
                    _sitWebSocket.Send(serializedData);
            }
            else
            {
                PooledBytesToPost.Add(serializedData);
            }
        }

        public void SendDataToPool(string url, Dictionary<string, object> data)
        {
            PooledDictionariesToPost.Add(new(url, data));
        }

        public void SendListDataToPool(string url, List<Dictionary<string, object>> data)
        {
            PooledDictionaryCollectionToPost.Add(data);
        }

        public int HostPing { get; set; } = 1;
        public int PostPing { get; set; } = 1;
        public ConcurrentQueue<int> PostPingSmooth { get; } = new();

        private Task PeriodicallySendPooledDataTask;

        private void PeriodicallySendPooledData()
        {
            //PatchConstants.Logger.LogDebug($"PeriodicallySendPooledData()");

            PeriodicallySendPooledDataTask = Task.Run(async () =>
            {
                int awaitPeriod = 1;
                //GCHelpers.EnableGC();
                //GCHelpers.ClearGarbage();
                //PatchConstants.Logger.LogDebug($"PeriodicallySendPooledData():In Async Task");

                //while (m_Instance != null)
                Stopwatch swPing = new();

                while (true)
                {
                    if (_sitWebSocket == null)
                    {
                        await Task.Delay(awaitPeriod);
                        continue;
                    }

                    swPing.Restart();
                    await Task.Delay(awaitPeriod);

                    if (PooledBytesToPost.Any())
                    {
                        if (_sitWebSocket != null)
                        {
                            while (PooledBytesToPost.TryTake(out var bytes))
                            {
                                //WebSocket.Send(bytes);
                                _sitWebSocket.Send(bytes);
                            }
                        }
                    }
                    //await Task.Delay(100);
                    while (PooledDictionariesToPost.Any())
                    {
                        await Task.Delay(awaitPeriod);

                        KeyValuePair<string, Dictionary<string, object>> d;
                        if (PooledDictionariesToPost.TryTake(out d))
                        {

                            var url = d.Key;
                            var json = JsonConvert.SerializeObject(d.Value);
                            _sitWebSocket.Send(json);

                            //var json = d.Value.ToJson();
                            //if (WebSocket != null)
                            //{
                            //    if (WebSocket.ReadyState == WebSocketSharp.WebSocketState.Open)
                            //    {
                            //        WebSocket.Send(json);
                            //    }
                            //    else
                            //    {
                            //        WebSocket_OnError();
                            //    }
                            //}
                        }
                    }

                    if (PooledDictionaryCollectionToPost.TryTake(out var d2))
                    {
                        var json = JsonConvert.SerializeObject(d2);
                        _sitWebSocket.Send(json);

                        //if (WebSocket != null)
                        //{
                        //    if (WebSocket.ReadyState == WebSocketSharp.WebSocketState.Open)
                        //    {
                        //        WebSocket.Send(json);
                        //    }
                        //    else
                        //    {
                        //        StayInTarkovHelperConstants.Logger.LogError($"WS:Periodic Send:PooledDictionaryCollectionToPost:Failed!");
                        //    }
                        //}
                        json = null;
                    }

                    while (PooledJsonToPostToUrl.Any())
                    {
                        await Task.Delay(awaitPeriod);

                        if (PooledJsonToPostToUrl.TryTake(out var kvp))
                        {
                            _ = await PostJsonAsync(kvp.Key, kvp.Value, timeout: 1000, debug: true);
                        }
                    }

                    if (PostPingSmooth.Any() && PostPingSmooth.Count > 30)
                        PostPingSmooth.TryDequeue(out _);

                    PostPingSmooth.Enqueue((int)swPing.ElapsedMilliseconds - awaitPeriod);
                    PostPing = (int)Math.Round(PostPingSmooth.Average());
                }
            });
        }

        private Task PeriodicallySendPingTask { get; set; }

        private void PeriodicallySendPing()
        {
            PeriodicallySendPingTask = Task.Run(async () =>
            {
                int awaitPeriod = 2000;
                while (true)
                {
                    await Task.Delay(awaitPeriod);

                    if (_sitWebSocket == null)
                        continue;

                    if (!CoopGameComponent.TryGetCoopGameComponent(out var coopGameComponent))
                        continue;

                    // PatchConstants.Logger.LogDebug($"WS:Ping Send");

                    var packet = new
                    {
                        m = "Ping",
                        t = DateTime.UtcNow.Ticks.ToString("G"),
                        profileId = coopGameComponent.OwnPlayer.ProfileId,
                        serverId = coopGameComponent.ServerId
                    };

                    _sitWebSocket.Send(Encoding.UTF8.GetBytes(packet.ToJson()));
                    packet = null;
                }
            });
        }

        private Dictionary<string, string> GetHeaders()
        {
            if (m_RequestHeaders != null && m_RequestHeaders.Count > 0)
                return m_RequestHeaders;

            string[] args = Environment.GetCommandLineArgs();

            foreach (string arg in args)
            {
                if (arg.Contains("-token="))
                {
                    Session = arg.Replace("-token=", string.Empty);
                    m_RequestHeaders = new Dictionary<string, string>()
                        {
                            { "Cookie", $"PHPSESSID={Session}" },
                            { "SessionId", Session }
                        };
                    break;
                }
            }
            return m_RequestHeaders;
        }

        /// <summary>
        /// Send request to the server and get Stream of data back
        /// </summary>
        /// <param name="url">String url endpoint example: /start</param>
        /// <param name="method">POST or GET</param>
        /// <param name="data">string json data</param>
        /// <param name="compress">Should use compression gzip?</param>
        /// <returns>Stream or null</returns>
        private MemoryStream SendAndReceive(string url, string method = "GET", string data = null, bool compress = true, int timeout = 9999, bool debug = false)
        {
            // Force to DEBUG mode if not Compressing.
            debug = debug || !compress;

            HttpClient.Timeout = TimeSpan.FromMilliseconds(timeout);


            method = method.ToUpper();

            var fullUri = url;
            if (!Uri.IsWellFormedUriString(fullUri, UriKind.Absolute))
                fullUri = RemoteEndPoint + fullUri;

            if (method == "GET")
            {
                var ms = new MemoryStream();
                var stream = HttpClient.GetStreamAsync(fullUri);
                stream.Result.CopyTo(ms);
                return ms;
            }
            else if (method == "POST" || method == "PUT")
            {
                var uri = new Uri(fullUri);
                return SendAndReceivePostOld(uri, method, data, compress, timeout, debug);
            }

            throw new ArgumentException($"Unknown method {method}");
        }

        /// <summary>
        /// Send request to the server and get Stream of data back by post
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="method"></param>
        /// <param name="data"></param>
        /// <param name="compress"></param>
        /// <param name="timeout"></param>
        /// <param name="debug"></param>
        /// <returns></returns>
        MemoryStream SendAndReceivePostOld(Uri uri, string method = "GET", string data = null, bool compress = true, int timeout = 9999, bool debug = false)
        {
            using (HttpClientHandler handler = new HttpClientHandler())
            {
                using(HttpClient httpClient = new HttpClient(handler))
                {
                    handler.UseCookies = true;
                    handler.CookieContainer = new CookieContainer();
                    httpClient.Timeout = TimeSpan.FromMilliseconds(timeout);
                    Uri baseAddress = new Uri(RemoteEndPoint);
                    foreach (var item in GetHeaders())
                    {
                        if (item.Key == "Cookie")
                        {
                            string[] pairs = item.Value.Split(';');
                            var keyValuePairs = pairs
                                .Select(p => p.Split(new[] { '=' }, 2))
                                .Where(kvp => kvp.Length == 2)
                                .ToDictionary(kvp => kvp[0], kvp => kvp[1]);
                            foreach (var kvp in keyValuePairs)
                            {
                                handler.CookieContainer.Add(baseAddress, new Cookie(kvp.Key, kvp.Value));
                            }
                        }
                        else
                        {
                            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(item.Key, item.Value);
                        }

                    }
                    if (!debug && method == "POST")
                    {
                        httpClient.DefaultRequestHeaders.AcceptEncoding.TryParseAdd("deflate");
                    }

                    HttpContent byteContent = null;
                    if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(data))
                    {
                        if (debug)
                        {
                            compress = false;
                            httpClient.DefaultRequestHeaders.Add("debug", "1");
                        }
                        var inputDataBytes = Encoding.UTF8.GetBytes(data);
                        byte[] bytes = compress ? Zlib.Compress(inputDataBytes) : inputDataBytes;
                        byteContent = new ByteArrayContent(bytes);
                        byteContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                        if (compress)
                        {
                            byteContent.Headers.ContentEncoding.Add("deflate");
                        }
                    }

                    HttpResponseMessage response;
                    if (byteContent != null)
                    {
                        response = httpClient.PostAsync(uri, byteContent).Result;
                    }
                    else
                    {
                        response = method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                            ? httpClient.PostAsync(uri, null).Result
                            : httpClient.GetAsync(uri).Result;
                    }

                    var ms = new MemoryStream();
                    if (response.IsSuccessStatusCode)
                    {
                        Stream responseStream = response.Content.ReadAsStreamAsync().Result;
                        responseStream.CopyTo(ms);
                        responseStream.Dispose();
                    }
                    else
                    {
                        StayInTarkovHelperConstants.Logger.LogError($"Unable to send api request to server.Status code" + response.StatusCode);
                    }

                    return ms;
                }

            }
        }

        public byte[] GetData(string url, bool hasHost = false)
        {
            using (var dataStream = SendAndReceive(url, "GET"))
                return dataStream.ToArray();
        }

        public void PutJson(string url, string data, bool compress = true, int timeout = 9999, bool debug = false)
        {
            using (Stream stream = SendAndReceive(url, "PUT", data, compress, timeout, debug)) { }
        }

        public string GetJson(string url, bool compress = true, int timeout = 9999)
        {
            using (MemoryStream stream = SendAndReceive(url, "GET", null, compress, timeout))
            {
                if (stream == null)
                    return "";
                var bytes = stream.ToArray();
                var result = Zlib.Decompress(bytes);
                bytes = null;
                return result;
            }
        }

        public string PostJson(string url, string data, bool compress = true, int timeout = 9999, bool debug = false)
        {
            using (MemoryStream stream = SendAndReceive(url, "POST", data, compress, timeout, debug))
            {
                if (stream == null)
                    return "";

                var bytes = stream.ToArray();
                string resultString;

                if (compress)
                {
                    if (Zlib.IsCompressed(bytes))
                        resultString = Zlib.Decompress(bytes);
                    else
                        resultString = Encoding.UTF8.GetString(bytes);
                }
                else
                {
                    resultString = Encoding.UTF8.GetString(bytes);
                }

                return resultString;
            }
        }

        public async Task<string> PostJsonAsync(string url, string data, bool compress = true, int timeout = DEFAULT_TIMEOUT_MS, bool debug = false, int retryAttempts = 5)
        {
            int attempt = 0;
            while (attempt++ < retryAttempts)
            {
                try
                {
                    return await Task.FromResult(PostJson(url, data, compress, timeout, debug));
                }
                catch (Exception ex)
                {
                    StayInTarkovHelperConstants.Logger.LogError(ex);
                }
            }
            throw new Exception($"Unable to communicate with Aki Server {url} to post json data: {data}");
        }

        public void PostJsonAndForgetAsync(string url, string data, bool compress = true, int timeout = DEFAULT_TIMEOUT_LONG_MS, bool debug = false)
        {
            SendDataToPool(url, data);
            //try
            //{
            //    _ = Task.Run(() => PostJson(url, data, compress, timeout, debug));
            //}
            //catch (Exception ex)
            //{
            //    PatchConstants.Logger.LogError(ex);
            //}
        }


        /// <summary>
        /// Retrieves data asyncronously and parses to the desired type
        /// </summary>
        /// <typeparam name="T">Desired type to Deserialize to</typeparam>
        /// <param name="url">URL to call</param>
        /// <param name="data">data to send</param>
        /// <returns></returns>
        public async Task<T> PostJsonAsync<T>(string url, string data, int timeout = DEFAULT_TIMEOUT_MS, int retryAttempts = 5, bool debug = true)
        {
            int attempt = 0;
            while (attempt++ < retryAttempts)
            {
                try
                {
                    var json = await PostJsonAsync(url, data, compress: false, timeout: timeout, debug);
                    return await Task.Run(() => JsonConvert.DeserializeObject<T>(json));
                }
                catch (Exception ex)
                {
                    StayInTarkovHelperConstants.Logger.LogError(ex);
                }
            }
            throw new Exception($"Unable to communicate with Aki Server {url} to post json data: {data}");
        }

        public void Dispose()
        {
            Session = null;
            RemoteEndPoint = null;
        }
    }
}
