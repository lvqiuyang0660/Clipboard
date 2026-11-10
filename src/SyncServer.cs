using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace ClipHelper
{
    /// <summary>服务器上登记的已配对设备（只有显示信息，没有任何密钥）。</summary>
    public class ServerDeviceInfo
    {
        public string Id;
        public string Name;
        public string Os;
        public bool Self;
        public bool Online;
        public int LastSeenSec;
    }

    /// <summary>
    /// 同步方式三：自建同步服务器（密文中转）。
    /// 服务端只存客户端已经加密好的帧，不持有明文与解密密钥；这里沿用局域网那套
    /// Cipher（AES-256-CBC + HMAC-SHA256）与 Msg 帧，只是把 TCP 连接换成 HTTP 收发。
    /// </summary>
    public partial class SyncService
    {
        public const int DefaultServerPort = 47820;

        Thread _relayThread;
        volatile bool _relayRunning;
        volatile bool _relayStop;
        DateTime _relayNextTryUtc = DateTime.MinValue;
        int _relayFailures;
        DateTime _nextPairPollUtc = DateTime.MinValue;
        bool _serverReachable;
        DateTime _lastServerOkUtc = DateTime.MinValue;
        long _serverLatencyMs = -1;
        DateTime _lastLatencyProbeUtc = DateTime.MinValue;
        List<ServerDeviceInfo> _serverDevices = new List<ServerDeviceInfo>();
        DateTime _serverDevicesUtc = DateTime.MinValue;
        volatile string _imgWaitHash;
        readonly ManualResetEvent _imgDone = new ManualResetEvent(false);
        string _imgResult;

        // ------------------------------------------------------------------ 配置
        public bool ServerConfigured
        {
            get { return !string.IsNullOrEmpty(_cfg.SyncServerHost); }
        }

        public string ServerHost { get { return _cfg.SyncServerHost == null ? "" : _cfg.SyncServerHost; } }

        public int ServerPort
        {
            get { return _cfg.SyncServerPort <= 0 ? DefaultServerPort : _cfg.SyncServerPort; }
        }

        public bool ServerHttps { get { return _cfg.SyncServerHttps; } }

        public bool ServerAllowSelfSigned { get { return _cfg.SyncServerAllowSelfSigned; } }

        public string ServerAddressText
        {
            get
            {
                if (!ServerConfigured) return "还没有填写服务器地址";
                return (ServerHttps ? "https" : "http") + "://" + ServerHost + ":" + ServerPort.ToString(CultureInfo.InvariantCulture);
            }
        }

        public bool ServerReachable { get { return _serverReachable; } }

        public string ServerLatencyText
        {
            get
            {
                if (!ServerConfigured) return "";
                if (!_serverReachable || _serverLatencyMs < 0) return "延迟：未知";
                return "延迟：" + _serverLatencyMs.ToString(CultureInfo.InvariantCulture) + " 毫秒";
            }
        }

        /// <summary>第一台：本机已经生成了配对码，正在等另一台输入。</summary>
        public bool ServerCodeActive
        {
            get
            {
                if (Peer != null) return false;
                if (string.IsNullOrEmpty(_cfg.SyncServerRoomId)) return false;
                if (string.IsNullOrEmpty(_cfg.SyncServerCode)) return false;
                return DateTime.UtcNow < _cfg.SyncServerCodeExpireUtc;
            }
        }

        public string ServerCode { get { return ServerCodeActive ? _cfg.SyncServerCode : ""; } }

        public int ServerCodeSecondsLeft
        {
            get
            {
                if (!ServerCodeActive) return 0;
                double s = (_cfg.SyncServerCodeExpireUtc - DateTime.UtcNow).TotalSeconds;
                if (s < 0) s = 0;
                return (int)Math.Ceiling(s);
            }
        }

        public List<ServerDeviceInfo> SnapshotServerDevices()
        {
            lock (_lock) { return new List<ServerDeviceInfo>(_serverDevices); }
        }

        /// <summary>保存服务器接入参数。地址可以填 IP 也可以填域名，允许连带协议头与端口一起粘贴。</summary>
        public void ConfigureServer(string rawAddress, int port, bool https, bool allowSelfSigned)
        {
            string host = (rawAddress == null ? "" : rawAddress.Trim());
            int parsedPort = 0;
            if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                host = host.Substring(7);
                https = false;
            }
            else if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                host = host.Substring(8);
                https = true;
            }
            int slash = host.IndexOf('/');
            if (slash >= 0) host = host.Substring(0, slash);
            int colon = host.LastIndexOf(':');
            if (colon > 0 && host.IndexOf(':') == colon)
            {
                string tail = host.Substring(colon + 1);
                if (int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedPort)) host = host.Substring(0, colon);
            }
            if (port <= 0) port = parsedPort > 0 ? parsedPort : DefaultServerPort;

            _cfg.SyncServerHost = host;
            _cfg.SyncServerPort = port;
            _cfg.SyncServerHttps = https;
            _cfg.SyncServerAllowSelfSigned = allowSelfSigned;
            _cfg.Save();
            _relayFailures = 0;
            _relayNextTryUtc = DateTime.MinValue;
            _lastLatencyProbeUtc = DateTime.MinValue;
            RaiseChanged();
        }

        // ------------------------------------------------------------------ 连通测试
        /// <summary>连通测试：成功返回一句话；失败返回具体原因（连不上 / 端口不通 / 配对码错 / 证书不受信）。</summary>
        public string TestServerConnection(string code)
        {
            if (!ServerConfigured) return "请先填写服务器地址，例如 47.100.12.34 或 sync.example.com";

            SrvCall r = Call("GET", "/v1/health", null, 9000);
            if (r.Error == "dns") return "域名解析失败：找不到「" + ServerHost + "」。检查地址是不是拼错了。";
            if (r.Error == "connect") return "连不上服务器：端口 " + ServerPort + " 不通。检查服务器程序是否在运行、云服务器「安全组」与系统防火墙是否放行这个端口。";
            if (r.Error == "timeout") return "连接超时：地址能到，但 " + ServerPort + " 端口没有回应，基本是被防火墙或安全组挡住了。";
            if (r.Error == "cert") return "服务器证书不受信。如果用的是自签名证书，请勾选下面的「信任这台服务器的自签名证书」再测一次。";
            if (r.Error != null) return "连接失败：" + r.Error;

            if (r.Status == 404 || r.Status == 400) return "这个端口能连上，但上面跑的不是剪贴板助手的同步服务，请检查端口号。";
            if (r.Status != 200) return "服务器返回异常状态（HTTP " + r.Status.ToString(CultureInfo.InvariantCulture) + "），稍后再试。";

            Dictionary<string, object> o = Json.ParseObject(r.Body);
            string svc = Json.GetString(o, "service");
            if (svc != "clipsync-relay") return "这个端口能连上，但上面跑的不是剪贴板助手的同步服务，请检查端口号。";

            string ver = Json.GetString(o, "version");
            string msg = "连接成功 · 服务器 " + (string.IsNullOrEmpty(ver) ? "版本未知" : "版本 " + ver)
                + " · 延迟 " + r.Ms.ToString(CultureInfo.InvariantCulture) + " 毫秒";

            _serverLatencyMs = r.Ms;
            _serverReachable = true;
            _lastServerOkUtc = DateTime.UtcNow;

            if (!string.IsNullOrEmpty(code) && code.Trim().Length == 6)
            {
                SrvCall v = Call("POST", "/v1/room/verify", "{\"code\":\"" + Json.Escape(code.Trim()) + "\"}", 9000);
                if (v.Status == 200) msg += " · 配对码有效";
                else if (v.Status == 429) msg += " · 配对码试错太频繁，请等 10 分钟再试";
                else msg += " · 配对码不正确或已过期";
            }
            RaiseChanged();
            return msg;
        }

        // ------------------------------------------------------------------ 配对：第一台生成配对码
        public string CreateServerRoom()
        {
            if (!ServerConfigured) return "请先填写服务器地址与端口。";
            if (Peer != null) return "这台电脑已经配对过了。想换设备，请先移除现有设备。";

            string token = NewToken();
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"deviceId\":\"").Append(Json.Escape(DeviceId)).Append("\"");
            sb.Append(",\"name\":\"").Append(Json.Escape(DeviceName)).Append("\"");
            sb.Append(",\"os\":\"").Append(Json.Escape(OsText)).Append("\"");
            sb.Append(",\"token\":\"").Append(token).Append("\"}");

            SrvCall r = Call("POST", "/v1/room/open", sb.ToString(), 12000);
            string err = ErrorText(r);
            if (err != null) return err;

            Dictionary<string, object> o = Json.ParseObject(r.Body);
            string roomId = Json.GetString(o, "roomId");
            string code = Json.GetString(o, "code");
            if (string.IsNullOrEmpty(roomId) || code.Length != 6) return "服务器返回的数据不完整，请稍后重试。";

            _cfg.SyncServerRoomId = roomId;
            _cfg.SyncServerToken = token;
            _cfg.SyncServerCode = code;
            _cfg.SyncServerCodeExpireUtc = DateTime.UtcNow.AddSeconds(Json.GetLong(o, "expireSec", 300));
            _cfg.SyncServerSeq = 0;
            _cfg.SyncServerOwner = true;
            _cfg.SyncServerPeerId = "";
            _cfg.Save();
            _serverReachable = true;
            RaiseChanged();
            return null;
        }

        /// <summary>配对码过期或想换一个：重新开一间房（旧房间里的待确认申请作废）。</summary>
        public string RefreshServerCode()
        {
            ClearServerRoom(true);
            return CreateServerRoom();
        }

        /// <summary>放弃这台电脑上的配对等待（不影响已经配好的设备）。</summary>
        public void CancelServerPairing()
        {
            ClearServerRoom(true);
            lock (_lock) { _pending.Clear(); }
            RaiseChanged();
        }

        // ------------------------------------------------------------------ 配对：第二台输入配对码
        public string JoinServerRoom(string code)
        {
            if (!ServerConfigured) return "请先填写服务器地址与端口。";
            code = (code == null ? "" : code.Trim());
            if (code.Length != 6) return "请输入另一台电脑上显示的 6 位配对码。";
            if (Peer != null) return "这台电脑已经配对过了。想重新配对，请先移除现有设备。";

            string token = NewToken();
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"code\":\"").Append(Json.Escape(code)).Append("\"");
            sb.Append(",\"deviceId\":\"").Append(Json.Escape(DeviceId)).Append("\"");
            sb.Append(",\"name\":\"").Append(Json.Escape(DeviceName)).Append("\"");
            sb.Append(",\"os\":\"").Append(Json.Escape(OsText)).Append("\"");
            sb.Append(",\"token\":\"").Append(token).Append("\"}");

            SrvCall r = Call("POST", "/v1/room/join", sb.ToString(), 12000);
            if (r.Status == 429) return "配对码试错次数太多，服务器暂时限制了这台电脑，请 10 分钟后再试。";
            if (r.Status == 404) return "配对码不正确，或者已经过期。请在另一台电脑上确认配对码后重试。";
            if (r.Status == 409)
            {
                Dictionary<string, object> eo = Json.ParseObject(r.Body);
                string c = Json.GetString(eo, "code");
                if (c == "room_full") return "对方设备已达上限（最多 2 台），需要先在对方电脑上移除一台。";
                if (c == "device_conflict") return "这台电脑已经在那台服务器的配对里了。要重新配对，请先在另一端移除它。";
                return "服务器拒绝了这次配对。";
            }
            string err = ErrorText(r);
            if (err != null) return err;

            Dictionary<string, object> o = Json.ParseObject(r.Body);
            string roomId = Json.GetString(o, "roomId");
            string state = Json.GetString(o, "state");
            if (string.IsNullOrEmpty(roomId)) return "服务器返回的数据不完整，请稍后重试。";

            _cfg.SyncServerRoomId = roomId;
            _cfg.SyncServerToken = token;
            _cfg.SyncServerCode = code;
            _cfg.SyncServerSeq = 0;
            _cfg.Save();
            RaiseChanged();

            if (state == "approved")
            {
                // 之前已经配对过（同一台电脑重复加入），直接取密钥
                string e0 = PullPairApproval(code);
                if (e0 != null) { ClearServerRoom(false); return e0; }
                return null;
            }

            // 等另一台点「允许加入」，最多 2 分钟
            DateTime deadline = DateTime.UtcNow.AddSeconds(118);
            while (DateTime.UtcNow < deadline && !_relayStop)
            {
                Thread.Sleep(1500);
                SrvCall st = Call("GET", "/v1/room/join?roomId=" + Uri.EscapeDataString(roomId)
                    + "&deviceId=" + Uri.EscapeDataString(DeviceId)
                    + "&token=" + Uri.EscapeDataString(token), null, 9000);
                if (st.Status == 429) return "请求太频繁，被服务器暂时限制，请稍后再试。";
                if (st.Error != null)
                {
                    if (st.Error == "connect" || st.Error == "timeout" || st.Error == "dns") continue;
                    return ErrorText(st);
                }
                Dictionary<string, object> so = Json.ParseObject(st.Body);
                string s = Json.GetString(so, "state");
                if (s == "approved")
                {
                    string e = PullPairApproval(code);
                    if (e == null) return null;
                    // 刚被允许时对方的密钥帧可能还没写入，稍等再取一次
                    Thread.Sleep(1200);
                    string e2 = PullPairApproval(code);
                    if (e2 != null) ClearServerRoom(false);
                    return e2;
                }
                if (s == "denied")
                {
                    ClearServerRoom(false);
                    return "对方没有允许这台电脑加入。核对一下配对码，让对方重新点一次「允许加入」。";
                }
            }
            return "等待对方确认超时。请在另一台电脑的「同步」页点「允许加入」后再试一次。";
        }

        /// <summary>从服务器取对方写下的配对确认（里面是加密后的共享密钥）。</summary>
        string PullPairApproval(string code)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                string path = "/v1/pull?roomId=" + Uri.EscapeDataString(_cfg.SyncServerRoomId)
                    + "&deviceId=" + Uri.EscapeDataString(DeviceId)
                    + "&token=" + Uri.EscapeDataString(_cfg.SyncServerToken)
                    + "&since=0&wait=6";
                SrvCall r = Call("GET", path, null, 12000);
                string err = ErrorText(r);
                if (err != null) return err;
                Dictionary<string, object> o = Json.ParseObject(r.Body);
                List<object> frames = Json.GetArray(o, "frames");
                Cipher byCode = new Cipher(DeriveFromCode(code));
                for (int i = 0; i < frames.Count; i++)
                {
                    Dictionary<string, object> f = frames[i] as Dictionary<string, object>;
                    if (f == null) continue;
                    long seq = Json.GetLong(f, "seq", 0);
                    string data = Json.GetString(f, "data");
                    byte[] raw;
                    try { raw = Convert.FromBase64String(data); } catch { continue; }
                    byte[] plain = byCode.Open(raw);
                    if (plain == null) continue;
                    Msg m = Msg.Parse(plain);
                    if (m == null || m.Type != "pairapprove") continue;
                    byte[] master;
                    try { master = Convert.FromBase64String(m.Get("key")); }
                    catch { return "配对确认里的密钥格式异常，请重新配对。"; }
                    if (master.Length != 32) return "配对确认里的密钥长度不对，请重新配对。";
                    string peerId = m.Raw("id");
                    if (string.IsNullOrEmpty(peerId)) peerId = _cfg.SyncServerPeerId;
                    if (string.IsNullOrEmpty(peerId)) return "配对确认里缺少设备信息，请重新配对。";
                    SavePeer(peerId, m.Get("name"), m.Get("os"), "", 0, master);
                    _cfg.SyncServerPeerId = peerId;
                    _cfg.SyncServerCode = "";
                    _cfg.SyncServerCodeExpireUtc = DateTime.MinValue;
                    // 游标停在配对确认这一条：排在它后面的历史内容留给正常同步流程，不会被跳过
                    _cfg.SyncServerSeq = seq;
                    _cfg.Save();
                    AckServer(seq);
                    _serverReachable = true;
                    _lastError = null;
                    RaiseChanged();
                    return null;
                }
                Thread.Sleep(1500);
            }
            return "对方已经允许加入，但密钥还没送到。请稍等几秒，再点一次「连通测试」确认服务器正常，然后重新触发同步。";
        }

        // ------------------------------------------------------------------ 第一台：允许 / 拒绝加入
        void DecideServerPending(string deviceId, bool allow)
        {
            PendingPair target = null;
            lock (_lock)
            {
                for (int i = 0; i < _pending.Count; i++)
                {
                    if (_pending[i].Id == deviceId) { target = _pending[i]; break; }
                }
            }
            if (target == null) return;

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"roomId\":\"").Append(Json.Escape(_cfg.SyncServerRoomId)).Append("\"");
            sb.Append(",\"deviceId\":\"").Append(Json.Escape(DeviceId)).Append("\"");
            sb.Append(",\"token\":\"").Append(Json.Escape(_cfg.SyncServerToken)).Append("\"");
            sb.Append(",\"target\":\"").Append(Json.Escape(deviceId)).Append("\"}");

            SrvCall r = Call("POST", allow ? "/v1/room/approve" : "/v1/room/deny", sb.ToString(), 12000);
            string err = ErrorText(r);
            lock (_lock) { _pending.Remove(target); }
            if (err != null)
            {
                SetError(err);
                RaiseChanged();
                return;
            }
            if (!allow)
            {
                RaiseChanged();
                return;
            }

            // 允许之后：生成共享密钥，用配对码派生的密钥加密，投递给对方
            byte[] master = RandomBytes(32);
            Cipher byCode = new Cipher(DeriveFromCode(_cfg.SyncServerCode));
            Msg ok = new Msg("pairapprove");
            ok.AddRaw("id", DeviceId);
            ok.Add("name", DeviceName);
            ok.Add("os", OsText);
            ok.Add("key", Convert.ToBase64String(master));
            List<string> frames = new List<string>();
            frames.Add(Convert.ToBase64String(byCode.Seal(ok.ToBytes())));
            string perr = PushFrames(frames);
            if (perr != null)
            {
                SetError("已允许加入，但密钥没能送到对方：" + perr);
                RaiseChanged();
                return;
            }
            SavePeer(deviceId, target.Name, target.Os, "", 0, master);
            _cfg.SyncServerPeerId = deviceId;
            _cfg.SyncServerCode = "";
            _cfg.SyncServerCodeExpireUtc = DateTime.MinValue;
            _cfg.Save();
            _lastError = null;
            RaiseChanged();
        }

        // ------------------------------------------------------------------ 后台循环
        void StartServerRelay()
        {
            if (_relayRunning) return;
            _relayStop = false;
            _relayRunning = true;
            _relayThread = new Thread(RelayLoop);
            _relayThread.IsBackground = true;
            _relayThread.Name = "ClipSync.Relay";
            _relayThread.Start();
            try { NetworkChange.NetworkAddressChanged += OnServerNetworkChanged; } catch { }
            RaiseChanged();
        }

        void StopServerRelay()
        {
            _relayRunning = false;
            _relayStop = true;
            try { NetworkChange.NetworkAddressChanged -= OnServerNetworkChanged; } catch { }
            RaiseChanged();
        }

        void OnServerNetworkChanged(object s, EventArgs e)
        {
            // 换 Wi-Fi / 拔网线：清掉在线状态与退避，下一轮自动重连
            _lastPeerSeenUtc = DateTime.MinValue;
            _serverReachable = false;
            _relayFailures = 0;
            _relayNextTryUtc = DateTime.MinValue;
            _lastLatencyProbeUtc = DateTime.MinValue;
            _dirty = true;
            RaiseChanged();
        }

        void RelayLoop()
        {
            while (_relayRunning)
            {
                try
                {
                    if (_cfg.SyncMode != "server") { Thread.Sleep(400); continue; }
                    DateTime now = DateTime.UtcNow;
                    if (now < _relayNextTryUtc) { Thread.Sleep(300); continue; }
                    if (_serverDevicesUtc == DateTime.MinValue || (now - _serverDevicesUtc).TotalSeconds > 6)
                        RefreshServerDevices(false);
                    SyncPeer peer = Peer;
                    if (peer == null)
                    {
                        PollPairingServer();
                        Thread.Sleep(900);
                        continue;
                    }
                    if (_cfg.SyncPaused || peer.Paused) { Thread.Sleep(900); continue; }
                    RelayOnce(peer);
                }
                catch (Exception)
                {
                    MarkRelayFailure();
                }
                Thread.Sleep(150);
            }
        }

        void MarkRelayFailure()
        {
            _relayFailures++;
            int wait = 2 + _relayFailures * 3;
            if (wait > 30) wait = 30;
            _relayNextTryUtc = DateTime.UtcNow.AddSeconds(wait);
            _serverReachable = false;
            // 状态栏要立刻反映"连不上"，而不是继续显示"同步中…"
            if (_lastError == null) SetError("连不上服务器，稍后自动重试");
            RaiseChanged();
        }

        /// <summary>还没配对时：轮询服务器，看有没有新设备用配对码申请加入。</summary>
        void PollPairingServer()
        {
            if (!_cfg.SyncServerOwner) return;
            if (string.IsNullOrEmpty(_cfg.SyncServerRoomId) || string.IsNullOrEmpty(_cfg.SyncServerToken)) return;
            if (DateTime.UtcNow < _nextPairPollUtc) return;
            _nextPairPollUtc = DateTime.UtcNow.AddSeconds(2);

            string path = "/v1/room/pending?roomId=" + Uri.EscapeDataString(_cfg.SyncServerRoomId)
                + "&deviceId=" + Uri.EscapeDataString(DeviceId)
                + "&token=" + Uri.EscapeDataString(_cfg.SyncServerToken);
            SrvCall r = Call("GET", path, null, 8000);
            if (r.Status == 403 || r.Status == 404)
            {
                ClearServerRoom(false);
                SetError("配对已失效，请重新生成配对码");
                return;
            }
            if (r.Error != null || r.Status != 200) return;

            _serverReachable = true;
            _lastServerOkUtc = DateTime.UtcNow;
            Dictionary<string, object> o = Json.ParseObject(r.Body);
            List<object> list = Json.GetArray(o, "pending");
            bool changed = false;
            lock (_lock)
            {
                List<string> alive = new List<string>();
                for (int i = 0; i < list.Count; i++)
                {
                    Dictionary<string, object> d = list[i] as Dictionary<string, object>;
                    if (d == null) continue;
                    string id = Json.GetString(d, "deviceId");
                    if (string.IsNullOrEmpty(id)) continue;
                    alive.Add(id);
                    bool exists = false;
                    for (int j = 0; j < _pending.Count; j++)
                    {
                        if (_pending[j].Id == id) { exists = true; break; }
                    }
                    if (exists) continue;
                    PendingPair p = new PendingPair();
                    p.Id = id;
                    p.Name = Json.GetString(d, "name");
                    if (string.IsNullOrEmpty(p.Name)) p.Name = "另一台电脑";
                    p.Os = Json.GetString(d, "os");
                    p.Address = "经服务器中转";
                    _pending.Add(p);
                    changed = true;
                    RaisePairRequested(p.Name);
                }
                // 对方取消 / 被允许的申请从列表移除
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    if (!alive.Contains(_pending[i].Id)) { _pending.RemoveAt(i); changed = true; }
                }
            }
            if (changed) RaiseChanged();
        }

        void RelayOnce(SyncPeer peer)
        {
            if (_syncing) return;
            Cipher cipher = PeerCipher(peer);
            if (cipher == null) { SetError("配对密钥不可用，请重新配对"); return; }

            _syncing = true;
            DateTime started = DateTime.UtcNow;
            try
            {
                // ---- 1) 把本轮之前的变更加密后推上去
                List<Msg> delta = BuildDelta(peer.LastSyncUtc);
                if (delta.Count > 0)
                {
                    List<string> frames = new List<string>();
                    for (int i = 0; i < delta.Count; i++) frames.Add(Convert.ToBase64String(cipher.Seal(delta[i].ToBytes())));
                    string perr = PushFrames(frames);
                    if (perr != null) { SetError(perr); MarkRelayFailure(); return; }
                    _serverReachable = true;
                    _lastServerOkUtc = DateTime.UtcNow;
                }

                // ---- 2) 长轮询拉对方推来的密文帧
                // 长轮询大部分时间在等，不算"同步中"，否则界面会一直显示"同步中…"
                _syncing = false;
                string path = "/v1/pull?roomId=" + Uri.EscapeDataString(_cfg.SyncServerRoomId)
                    + "&deviceId=" + Uri.EscapeDataString(DeviceId)
                    + "&token=" + Uri.EscapeDataString(_cfg.SyncServerToken)
                    + "&since=" + _cfg.SyncServerSeq.ToString(CultureInfo.InvariantCulture)
                    + "&wait=5";
                SrvCall r = Call("GET", path, null, 15000);
                if (r.Status == 403)
                {
                    HandleServerRevoked();
                    return;
                }
                string err = ErrorText(r);
                if (err != null) { SetError(err); MarkRelayFailure(); return; }

                Dictionary<string, object> o = Json.ParseObject(r.Body);
                long next = Json.GetLong(o, "next", _cfg.SyncServerSeq);
                bool revoked = Json.GetBool(o, "revoked", false);
                if (revoked) { HandleServerRevoked(); return; }
                bool online = Json.GetBool(o, "peerOnline", false);
                int seen = (int)Json.GetLong(o, "peerSeenSec", -1);

                List<Msg> batch = new List<Msg>();
                List<string> imgReq = new List<string>();
                List<object> framesIn = Json.GetArray(o, "frames");
                long maxSeq = _cfg.SyncServerSeq;
                for (int i = 0; i < framesIn.Count; i++)
                {
                    Dictionary<string, object> f = framesIn[i] as Dictionary<string, object>;
                    if (f == null) continue;
                    long seq = Json.GetLong(f, "seq", 0);
                    if (seq > maxSeq) maxSeq = seq;
                    byte[] raw;
                    try { raw = Convert.FromBase64String(Json.GetString(f, "data")); }
                    catch { continue; }
                    byte[] plain = cipher.Open(raw);
                    if (plain == null) continue;
                    Msg m = Msg.Parse(plain);
                    if (m == null) continue;
                    if (m.Type == "item" || m.Type == "tomb") batch.Add(m);
                    else if (m.Type == "reqimg") imgReq.Add(m.Raw("hash"));
                    else if (m.Type == "img") HandleIncomingImage(m);
                    else if (m.Type == "imgerr") HandleIncomingImageError(m);
                }

                if (batch.Count > 0)
                {
                    _syncing = true;
                    try { ApplyBatch(batch); }
                    finally { _syncing = false; }
                }
                for (int i = 0; i < imgReq.Count; i++) SendImageServer(cipher, imgReq[i]);

                DateTime now = DateTime.UtcNow;
                if (maxSeq > _cfg.SyncServerSeq)
                {
                    _cfg.SyncServerSeq = maxSeq;
                    SaveThrottled();
                    AckServer(maxSeq);
                }
                if (next > maxSeq) { _cfg.SyncServerSeq = next; SaveThrottled(); }

                peer.LastSyncUtc = started;
                _lastSyncUtc = started;
                if (online) _lastPeerSeenUtc = now;
                else if (seen >= 0) _lastPeerSeenUtc = now.AddSeconds(-seen);
                _lastError = null;
                _serverReachable = true;
                _lastServerOkUtc = now;
                _relayFailures = 0;
                _dirty = false;
                SaveThrottled();
                RaiseChanged();
            }
            catch (Exception)
            {
                SetError("同步中断，稍后自动重试");
                MarkRelayFailure();
            }
            finally
            {
                _syncing = false;
            }
        }

        void HandleServerRevoked()
        {
            string peerName = PeerName;
            ClearServerRoom(false);
            RemovePeer(Peer == null ? null : Peer.Id);
            _lastError = "你已被移出同步" + (string.IsNullOrEmpty(peerName) ? "" : "（" + peerName + "）") + "，本地记录完整保留";
            _lastErrorUtc = DateTime.UtcNow;
            RaiseChanged();
        }

        /// <summary>本机在服务器上的配对信息（房间、令牌、游标）全部清掉。</summary>
        void ClearServerRoom(bool revokeSelf)
        {
            if (revokeSelf && !string.IsNullOrEmpty(_cfg.SyncServerRoomId) && !string.IsNullOrEmpty(_cfg.SyncServerToken))
                PostRevoke(DeviceId);
            _cfg.SyncServerRoomId = "";
            _cfg.SyncServerToken = "";
            _cfg.SyncServerCode = "";
            _cfg.SyncServerCodeExpireUtc = DateTime.MinValue;
            _cfg.SyncServerSeq = 0;
            _cfg.SyncServerPeerId = "";
            _cfg.SyncServerOwner = false;
            _cfg.Save();
            lock (_lock) { _serverDevices.Clear(); }
        }

        /// <summary>解除配对前先把对方从服务器白名单里吊销，避免它继续往房间里写密文。</summary>
        void ServerBeforePeerRemoved(string peerId)
        {
            if (string.IsNullOrEmpty(peerId)) return;
            PostRevoke(peerId);
        }

        void PostRevoke(string target)
        {
            if (string.IsNullOrEmpty(_cfg.SyncServerRoomId) || string.IsNullOrEmpty(_cfg.SyncServerToken)) return;
            if (string.IsNullOrEmpty(target)) return;
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"roomId\":\"").Append(Json.Escape(_cfg.SyncServerRoomId)).Append("\"");
            sb.Append(",\"deviceId\":\"").Append(Json.Escape(DeviceId)).Append("\"");
            sb.Append(",\"token\":\"").Append(Json.Escape(_cfg.SyncServerToken)).Append("\"");
            sb.Append(",\"target\":\"").Append(Json.Escape(target)).Append("\"}");
            Call("POST", "/v1/room/revoke", sb.ToString(), 6000);
        }

        // ------------------------------------------------------------------ 设备列表与吊销
        void RefreshServerDevices(bool force)
        {
            if (_cfg.SyncMode != "server") return;
            if (string.IsNullOrEmpty(_cfg.SyncServerRoomId) || string.IsNullOrEmpty(_cfg.SyncServerToken)) return;
            if (!force && (DateTime.UtcNow - _serverDevicesUtc).TotalSeconds < 4) return;
            _serverDevicesUtc = DateTime.UtcNow;

            string path = "/v1/room/devices?roomId=" + Uri.EscapeDataString(_cfg.SyncServerRoomId)
                + "&deviceId=" + Uri.EscapeDataString(DeviceId)
                + "&token=" + Uri.EscapeDataString(_cfg.SyncServerToken);
            SrvCall r = Call("GET", path, null, 8000);
            if (r.Error != null || r.Status != 200) return;
            Dictionary<string, object> o = Json.ParseObject(r.Body);
            List<object> list = Json.GetArray(o, "devices");
            List<ServerDeviceInfo> result = new List<ServerDeviceInfo>();
            for (int i = 0; i < list.Count; i++)
            {
                Dictionary<string, object> d = list[i] as Dictionary<string, object>;
                if (d == null) continue;
                ServerDeviceInfo info = new ServerDeviceInfo();
                info.Id = Json.GetString(d, "deviceId");
                info.Name = Json.GetString(d, "name");
                info.Os = Json.GetString(d, "os");
                info.Self = Json.GetBool(d, "self", false);
                info.Online = Json.GetBool(d, "online", false);
                info.LastSeenSec = (int)Json.GetLong(d, "lastSeenSec", -1);
                result.Add(info);
            }
            lock (_lock) { _serverDevices = result; }
        }

        /// <summary>吊销一台设备（立即作废它在服务器上的收件箱与白名单）。</summary>
        public string RevokeServerDevice(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return "缺少设备。";
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"roomId\":\"").Append(Json.Escape(_cfg.SyncServerRoomId)).Append("\"");
            sb.Append(",\"deviceId\":\"").Append(Json.Escape(DeviceId)).Append("\"");
            sb.Append(",\"token\":\"").Append(Json.Escape(_cfg.SyncServerToken)).Append("\"");
            sb.Append(",\"target\":\"").Append(Json.Escape(deviceId)).Append("\"}");
            SrvCall r = Call("POST", "/v1/room/revoke", sb.ToString(), 10000);
            string err = ErrorText(r);
            if (err != null) return err;
            RefreshServerDevices(true);
            SyncPeer peer = Peer;
            if (peer != null && peer.Id == deviceId)
            {
                RemovePeer(deviceId);
                _lastSyncUtc = DateTime.MinValue;
                _lastError = "已吊销该设备，需要重新配对才能同步";
                _lastErrorUtc = DateTime.UtcNow;
            }
            RaiseChanged();
            return null;
        }

        // ------------------------------------------------------------------ 按需取图（经服务器）
        string RequestImageServer(ClipItem it)
        {
            SyncPeer peer = Peer;
            if (peer == null) return "这张图片来自另一台电脑，当前没有配对设备，无法获取。";
            if (peer.Paused || _cfg.SyncPaused) return "同步已暂停：恢复这台设备的同步后才能获取图片。";
            if (_imageBusyHash == it.Hash) return "正在获取这张图片…";
            _imageBusyHash = it.Hash;
            string hash = it.Hash;
            string sizeText = TextUtil.SizeText(it.Bytes);
            string peerName = peer.Name;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = FetchImageServer(hash);
                _imageBusyHash = null;
                if (err != null) SetError(err);
                else { _lastError = null; RaiseChanged(); }
            });
            return "正在从 " + peerName + " 获取图片…（" + sizeText + "）";
        }

        string FetchImageServer(string hash)
        {
            SyncPeer peer = Peer;
            if (peer == null) return "配对已断开";
            Cipher cipher = PeerCipher(peer);
            if (cipher == null) return "配对密钥不可用";
            _imgWaitHash = hash;
            _imgResult = null;
            _imgDone.Reset();

            Msg req = new Msg("reqimg");
            req.AddRaw("hash", hash);
            List<string> frames = new List<string>();
            frames.Add(Convert.ToBase64String(cipher.Seal(req.ToBytes())));
            string err = PushFrames(frames);
            if (err != null) { _imgWaitHash = null; return err; }

            bool ok = _imgDone.WaitOne(25000, false);
            _imgWaitHash = null;
            if (!ok) return "对方没有返回图片（可能离线或已删除），稍后再点一次";
            return _imgResult;
        }

        void HandleIncomingImage(Msg m)
        {
            string hash = m.Raw("hash");
            if (_imgWaitHash == null || _imgWaitHash != hash) return;
            byte[] png;
            try { png = Convert.FromBase64String(m.Get("data")); }
            catch { _imgResult = "收到的图片数据不完整"; _imgDone.Set(); return; }
            if (png.Length > 20 * 1024 * 1024) { _imgResult = "图片太大（超过 20 MB），未获取"; _imgDone.Set(); return; }
            ClipItem item = _store.FindByHash(hash);
            bool saved = false;
            _applying = true;
            try { if (item != null) saved = _store.AttachImage(item, png); }
            finally { _applying = false; }
            _imgResult = saved ? null : "图片保存失败";
            _imgDone.Set();
        }

        void HandleIncomingImageError(Msg m)
        {
            string hash = m.Raw("hash");
            if (_imgWaitHash == null || _imgWaitHash != hash) return;
            string reason = m.Raw("reason");
            if (reason == "missing") _imgResult = "对方已经没有这张图片了";
            else if (reason == "toolarge") _imgResult = "图片太大（超过 20 MB），未获取";
            else _imgResult = "对方读取图片失败";
            _imgDone.Set();
        }

        void SendImageServer(Cipher cipher, string hash)
        {
            ClipItem it = _store.FindByHash(hash);
            if (it == null || !it.IsImage || !_store.HasLocalImage(it))
            {
                PushOne(cipher, new Msg("imgerr").AddRaw("hash", hash).AddRaw("reason", "missing"));
                return;
            }
            try
            {
                byte[] png = File.ReadAllBytes(_store.ImagePath(it));
                if (png.Length > 20 * 1024 * 1024)
                {
                    PushOne(cipher, new Msg("imgerr").AddRaw("hash", hash).AddRaw("reason", "toolarge"));
                    return;
                }
                Msg img = new Msg("img");
                img.AddRaw("hash", hash);
                img.Add("data", Convert.ToBase64String(png));
                PushOne(cipher, img);
            }
            catch
            {
                PushOne(cipher, new Msg("imgerr").AddRaw("hash", hash).AddRaw("reason", "io"));
            }
        }

        void PushOne(Cipher cipher, Msg m)
        {
            List<string> frames = new List<string>();
            frames.Add(Convert.ToBase64String(cipher.Seal(m.ToBytes())));
            PushFrames(frames);
        }

        // ------------------------------------------------------------------ HTTP 基础
        class SrvCall
        {
            public int Status;
            public string Body = "";
            public string Error;
            public long Ms;
        }

        string PushFrames(List<string> frames)
        {
            if (string.IsNullOrEmpty(_cfg.SyncServerRoomId)) return "还没有配对";
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"roomId\":\"").Append(Json.Escape(_cfg.SyncServerRoomId)).Append("\"");
            sb.Append(",\"deviceId\":\"").Append(Json.Escape(DeviceId)).Append("\"");
            sb.Append(",\"token\":\"").Append(Json.Escape(_cfg.SyncServerToken)).Append("\"");
            sb.Append(",\"frames\":[");
            for (int i = 0; i < frames.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(frames[i]).Append('"');
            }
            sb.Append("]}");
            SrvCall r = Call("POST", "/v1/push", sb.ToString(), 40000);
            if (r.Status == 403) return "已在服务器上被移出配对";
            string err = ErrorText(r);
            if (err == null && r.Ms > 0) _serverLatencyMs = r.Ms;
            return err;
        }

        void AckServer(long upTo)
        {
            if (upTo <= 0) return;
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"roomId\":\"").Append(Json.Escape(_cfg.SyncServerRoomId)).Append("\"");
            sb.Append(",\"deviceId\":\"").Append(Json.Escape(DeviceId)).Append("\"");
            sb.Append(",\"token\":\"").Append(Json.Escape(_cfg.SyncServerToken)).Append("\"");
            sb.Append(",\"upTo\":").Append(upTo.ToString(CultureInfo.InvariantCulture)).Append("}");
            Call("POST", "/v1/ack", sb.ToString(), 8000);
        }

        /// <summary>把服务器返回的失败翻译成给用户看的一句话；成功返回 null。</summary>
        string ErrorText(SrvCall r)
        {
            if (r == null) return "连接服务器失败";
            if (r.Error == "dns") return "域名解析失败：找不到「" + ServerHost + "」";
            if (r.Error == "connect") return "连不上服务器（端口 " + ServerPort + " 不通），检查网络、安全组与防火墙";
            if (r.Error == "timeout") return "连接服务器超时，稍后自动重试";
            if (r.Error == "cert") return "服务器证书不受信；自签名证书请勾选「信任自签名证书」";
            if (r.Error != null) return "连接服务器失败，稍后自动重试";
            if (r.Status == 200) return null;

            Dictionary<string, object> o = Json.ParseObject(r.Body);
            string code = Json.GetString(o, "code");
            if (r.Status == 403)
            {
                if (code == "revoked" || code == "no_room" || code == "bad_token") return "已在服务器上被移出配对";
                return "服务器拒绝了这次请求";
            }
            if (r.Status == 404) return "配对已失效，请重新配对";
            if (r.Status == 429) return "尝试次数过多，服务器暂时限制，请 10 分钟后再试";
            if (r.Status == 409)
            {
                string msg = Json.GetString(o, "message");
                return string.IsNullOrEmpty(msg) ? "服务器拒绝了这次请求" : msg;
            }
            if (r.Status >= 500) return "服务器出错（HTTP " + r.Status.ToString(CultureInfo.InvariantCulture) + "），稍后重试";
            return "服务器返回异常（HTTP " + r.Status.ToString(CultureInfo.InvariantCulture) + "）";
        }

        static bool _tlsReady;
        static volatile bool _allowSelfSigned;
        static volatile string _pinnedHost;

        static void EnsureTls()
        {
            if (_tlsReady) return;
            _tlsReady = true;
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768; }
            catch { }
            try { ServicePointManager.ServerCertificateValidationCallback = OnCertCheck; }
            catch { }
        }

        static bool OnCertCheck(object sender, X509Certificate cert, X509Chain chain, System.Net.Security.SslPolicyErrors errors)
        {
            if (errors == System.Net.Security.SslPolicyErrors.None) return true;
            if (!_allowSelfSigned) return false;
            HttpWebRequest req = sender as HttpWebRequest;
            if (req == null || req.Address == null) return false;
            string pinned = _pinnedHost;
            if (string.IsNullOrEmpty(pinned)) return false;
            return string.Equals(req.Address.Host, pinned, StringComparison.OrdinalIgnoreCase);
        }

        SrvCall Call(string method, string path, string body, int timeoutMs)
        {
            EnsureTls();
            _allowSelfSigned = _cfg.SyncServerAllowSelfSigned;
            _pinnedHost = _cfg.SyncServerHost;

            SrvCall r = new SrvCall();
            string url = (ServerHttps ? "https" : "http") + "://" + ServerHost + ":" + ServerPort.ToString(CultureInfo.InvariantCulture) + path;
            DateTime t0 = DateTime.UtcNow;
            HttpWebRequest req = null;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.AllowAutoRedirect = false;
                req.KeepAlive = false;
                req.Proxy = null;
                req.UserAgent = "ClipboardHelper-Sync/1.0";
                if (body != null)
                {
                    byte[] data = Encoding.UTF8.GetBytes(body);
                    req.ContentType = "application/json; charset=utf-8";
                    req.ContentLength = data.Length;
                    using (Stream s = req.GetRequestStream()) { s.Write(data, 0, data.Length); }
                }
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    r.Status = (int)resp.StatusCode;
                    r.Body = ReadAll(resp.GetResponseStream());
                }
            }
            catch (WebException we)
            {
                HttpWebResponse resp = we.Response as HttpWebResponse;
                if (resp != null)
                {
                    r.Status = (int)resp.StatusCode;
                    try { r.Body = ReadAll(resp.GetResponseStream()); } catch { }
                    try { resp.Close(); } catch { }
                }
                else
                {
                    r.Error = MapWebError(we);
                }
            }
            catch (Exception)
            {
                r.Error = "io";
            }
            finally
            {
                r.Ms = (long)(DateTime.UtcNow - t0).TotalMilliseconds;
                try { if (req != null) req.Abort(); } catch { }
            }
            return r;
        }

        static string MapWebError(WebException we)
        {
            if (we.Status == WebExceptionStatus.NameResolutionFailure) return "dns";
            if (we.Status == WebExceptionStatus.ProxyNameResolutionFailure) return "dns";
            if (we.Status == WebExceptionStatus.ConnectFailure) return "connect";
            if (we.Status == WebExceptionStatus.Timeout) return "timeout";
            if (we.Status == WebExceptionStatus.TrustFailure) return "cert";
            if (we.Status == WebExceptionStatus.SecureChannelFailure) return "cert";
            if (we.InnerException is AuthenticationException) return "cert";
            return "io";
        }

        static string ReadAll(Stream s)
        {
            if (s == null) return "";
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buf = new byte[16384];
                while (true)
                {
                    int n;
                    try { n = s.Read(buf, 0, buf.Length); }
                    catch { break; }
                    if (n <= 0) break;
                    ms.Write(buf, 0, n);
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        static string NewToken()
        {
            byte[] b = RandomBytes(32);
            StringBuilder sb = new StringBuilder(b.Length * 2);
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>够用就行的 JSON 读写：服务端返回的都是固定的小结构，不引入额外依赖。</summary>
    public static class Json
    {
        public static string Escape(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            object o = Parse(text);
            return o as Dictionary<string, object>;
        }

        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int pos = 0;
            try { return ParseValue(text, ref pos); }
            catch { return null; }
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{')
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                i++;
                while (true)
                {
                    SkipWs(s, ref i);
                    if (i >= s.Length) break;
                    if (s[i] == '}') { i++; break; }
                    if (s[i] == ',') { i++; continue; }
                    string key = ParseString(s, ref i);
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ':') i++;
                    object v = ParseValue(s, ref i);
                    if (key != null) d[key] = v;
                }
                return d;
            }
            if (c == '[')
            {
                List<object> list = new List<object>();
                i++;
                while (true)
                {
                    SkipWs(s, ref i);
                    if (i >= s.Length) break;
                    if (s[i] == ']') { i++; break; }
                    if (s[i] == ',') { i++; continue; }
                    list.Add(ParseValue(s, ref i));
                }
                return list;
            }
            if (c == '"') return ParseString(s, ref i);
            if (c == 't')
            {
                if (i + 4 <= s.Length && s.Substring(i, 4) == "true") { i += 4; return true; }
                i++;
                return false;
            }
            if (c == 'f')
            {
                if (i + 5 <= s.Length && s.Substring(i, 5) == "false") { i += 5; return false; }
                i++;
                return false;
            }
            if (c == 'n')
            {
                if (i + 4 <= s.Length && s.Substring(i, 4) == "null") { i += 4; }
                return null;
            }
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;
            if (i == start) { i++; return null; }
            double d2;
            if (double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d2)) return d2;
            return null;
        }

        static string ParseString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') return null;
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"') { i++; break; }
                if (c == '\\' && i + 1 < s.Length)
                {
                    i++;
                    char n = s[i];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 'r') sb.Append('\r');
                    else if (n == 't') sb.Append('\t');
                    else if (n == 'b') sb.Append('\b');
                    else if (n == 'f') sb.Append('\f');
                    else if (n == 'u' && i + 4 < s.Length)
                    {
                        int code;
                        if (int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                    }
                    else sb.Append(n);
                    i++;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        public static string GetString(Dictionary<string, object> o, string key)
        {
            if (o == null) return "";
            object v;
            if (!o.TryGetValue(key, out v) || v == null) return "";
            return v as string;
        }

        public static long GetLong(Dictionary<string, object> o, string key, long def)
        {
            if (o == null) return def;
            object v;
            if (!o.TryGetValue(key, out v) || v == null) return def;
            if (v is double) return (long)(double)v;
            if (v is string)
            {
                long n;
                if (long.TryParse((string)v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            }
            return def;
        }

        public static bool GetBool(Dictionary<string, object> o, string key, bool def)
        {
            if (o == null) return def;
            object v;
            if (!o.TryGetValue(key, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            return def;
        }

        public static List<object> GetArray(Dictionary<string, object> o, string key)
        {
            if (o == null) return new List<object>();
            object v;
            if (!o.TryGetValue(key, out v)) return new List<object>();
            List<object> list = v as List<object>;
            if (list == null) return new List<object>();
            return list;
        }
    }
}
