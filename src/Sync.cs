using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;

namespace ClipHelper
{
    /// <summary>局域网里发现到的、还没配对的电脑。</summary>
    public class Discovered
    {
        public string Id;
        public string Name;
        public string Os;
        public string Address;
        public int Port;
        public DateTime SeenUtc;
    }

    /// <summary>对方发来的加入请求，等本机点「允许加入 / 拒绝」。</summary>
    public class PendingPair
    {
        public string Id;
        public string Name;
        public string Os;
        public string Address;
        public string Code;
        public TcpClient Client;
        public ManualResetEvent Decided = new ManualResetEvent(false);
        public bool Allow;
    }

    /// <summary>共享文件夹里发现到的配对邀请。</summary>
    public class PairInvite
    {
        public string Id;
        public string Name;
        public string Os;
        public DateTime ExpireUtc;
        public string FilePath;
        public byte[] Salt;
        public byte[] Blob;
    }

    /// <summary>
    /// 局域网同步：UDP 组播发现 + TCP 直连同步。
    /// 配对时用 6 位配对码派生临时密钥交换长期密钥，之后所有内容帧都走 AES-256-CBC + HMAC-SHA256。
    /// 图片只同步元数据，原图要靠用户在列表里点「获取」才拉取。
    /// </summary>
    public partial class SyncService
    {
        public const int DiscoveryPort = 47033;
        const string GroupAddress = "239.255.42.99";
        const int MaxFrame = 40 * 1024 * 1024;
        const int MaxPeers = 1;                 // 首发上限 2 台：本机 + 1 台

        readonly AppSettings _cfg;
        readonly Store _store;
        readonly object _lock = new object();
        readonly object _countLock = new object();

        TcpListener _server;
        int _port;
        UdpClient _udp;
        UdpClient _bcast;
        Thread _discoveryThread;
        Thread _loopThread;
        volatile bool _running;

        readonly Dictionary<string, Discovered> _seen = new Dictionary<string, Discovered>();
        readonly List<PendingPair> _pending = new List<PendingPair>();
        readonly Dictionary<string, Cipher> _ciphers = new Dictionary<string, Cipher>();

        string _code;
        DateTime _codeExpireUtc = DateTime.MinValue;
        DateTime _lastPeerSeenUtc = DateTime.MinValue;
        DateTime _lastSyncUtc = DateTime.MinValue;
        DateTime _lastErrorUtc = DateTime.MinValue;
        string _lastError;
        bool _syncing;
        bool _dirty = true;
        bool _applying;
        DateTime _lastUiUtc = DateTime.MinValue;
        DateTime _lastSaveUtc = DateTime.MinValue;
        string _imageBusyHash;
        DateTime _joinSentUtc = DateTime.MinValue;

        // 共享文件夹配对（跨网络）：一台写邀请，另一台用配对码确认后写回执
        FolderPairing _folderPending;
        DateTime _lastFolderScanUtc = DateTime.MinValue;
        List<PairInvite> _inviteCache = new List<PairInvite>();
        DateTime _inviteCacheUtc = DateTime.MinValue;
        string _folderError;

        class FolderPairing
        {
            public string Code;
            public byte[] Master;
            public DateTime ExpireUtc;
            public string InvitePath;
        }

        public event Action Changed;
        public event Action<string> PairRequested;   // 有设备请求加入（参数是设备名），必须提醒用户

        public SyncService(AppSettings cfg, Store store)
        {
            _cfg = cfg;
            _store = store;
            EnsureIdentity();
            // 自己订阅本机历史的变更：新增/改标记要推给对方，删除要写删除标记
            _store.Changed += OnStoreChanged;
            _store.Deleted += OnStoreDeleted;
            _store.Restored += OnStoreRestored;
        }

        void OnStoreChanged()
        {
            NotifyStoreChanged();
        }

        void OnStoreDeleted(ClipItem it)
        {
            NotifyLocalDeleted(it);
        }

        void OnStoreRestored(ClipItem it)
        {
            NotifyLocalRestored(it);
        }

        // ------------------------------------------------------------------ 设备身份
        public string DeviceId
        {
            get { EnsureIdentity(); return _cfg.SyncDeviceId; }
        }

        public string DeviceName
        {
            get { EnsureIdentity(); return _cfg.SyncDeviceName; }
        }

        void EnsureIdentity()
        {
            bool changed = false;
            if (string.IsNullOrEmpty(_cfg.SyncDeviceId))
            {
                _cfg.SyncDeviceId = Guid.NewGuid().ToString("N");
                changed = true;
            }
            if (string.IsNullOrEmpty(_cfg.SyncDeviceName))
            {
                string name = "";
                try { name = Environment.MachineName; } catch { }
                if (string.IsNullOrEmpty(name)) name = "我的电脑";
                _cfg.SyncDeviceName = name;
                changed = true;
            }
            if (changed) _cfg.Save();
        }

        /// <summary>界面展示用系统版本，不含补丁号，避免暴露过多。</summary>
        public static string OsText
        {
            get
            {
                string v = "Windows";
                try
                {
                    Version ver = Environment.OSVersion.Version;
                    if (ver.Major == 10) v = "Windows 10 / 11";
                    else if (ver.Major == 6 && ver.Minor == 3) v = "Windows 8.1";
                    else if (ver.Major == 6 && ver.Minor == 2) v = "Windows 8";
                    else if (ver.Major == 6 && ver.Minor == 1) v = "Windows 7";
                    else v = "Windows " + ver.Major + "." + ver.Minor;
                }
                catch { }
                try { if (Environment.Is64BitOperatingSystem) v += " · 64 位"; } catch { }
                return v;
            }
        }

        public static string MaskAddress(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return "未知地址";
            string[] p = ip.Split('.');
            if (p.Length == 4) return p[0] + "." + p[1] + ".••.••";
            return "局域网地址";
        }

        // ------------------------------------------------------------------ 状态
        public SyncPeer Peer
        {
            get
            {
                lock (_lock)
                {
                    return _cfg.SyncPeers.Count > 0 ? _cfg.SyncPeers[0] : null;
                }
            }
        }

        public string PeerName
        {
            get { SyncPeer p = Peer; return p == null ? "" : DisplayName(p); }
        }

        static string DisplayName(SyncPeer p)
        {
            if (p == null) return "";
            if (!string.IsNullOrEmpty(p.Alias)) return p.Alias;
            return string.IsNullOrEmpty(p.Name) ? "另一台电脑" : p.Name;
        }

        public void RenameLocal(string name)
        {
            name = (name == null ? "" : name.Trim());
            if (name.Length == 0 || name.Length > 24) return;
            _cfg.SyncDeviceName = name;
            _cfg.Save();
            _dirty = true;
            RaiseChanged();
        }

        /// <summary>改对方设备在本机的显示名，只影响这台电脑上的显示。</summary>
        public void RenamePeer(string alias)
        {
            SyncPeer p = Peer;
            if (p == null) return;
            alias = (alias == null ? "" : alias.Trim());
            if (alias.Length > 24) alias = alias.Substring(0, 24);
            p.Alias = alias;
            _cfg.Save();
            RaiseChanged();
        }

        public bool IsPaired { get { return Peer != null; } }

        public bool PeerOnline
        {
            get
            {
                SyncPeer p = Peer;
                if (p == null) return false;
                if (p.Paused || _cfg.SyncPaused) return false;
                return (DateTime.UtcNow - _lastPeerSeenUtc).TotalSeconds < 12;
            }
        }

        public DateTime LastSyncLocal
        {
            get
            {
                if (_lastSyncUtc != DateTime.MinValue) return _lastSyncUtc.ToLocalTime();
                SyncPeer p = Peer;
                if (p != null && p.LastSyncUtc != DateTime.MinValue) return p.LastSyncUtc.ToLocalTime();
                return DateTime.MinValue;
            }
        }

        public int OutboxCount
        {
            get
            {
                SyncPeer p = Peer;
                if (p == null) return 0;
                DateTime since = p.LastSyncUtc;
                int n = 0;
                foreach (ClipItem it in SnapshotItems())
                {
                    if (since == DateTime.MinValue || it.Modified.ToUniversalTime() > since) n++;
                }
                foreach (SyncTombstone t in _cfg.SyncDeleted)
                {
                    if (since == DateTime.MinValue || t.DeletedUtc > since) n++;
                }
                return n;
            }
        }

        public bool Syncing { get { return _syncing; } }

        public bool JoinWaiting
        {
            get { return _joinSentUtc != DateTime.MinValue && (DateTime.UtcNow - _joinSentUtc).TotalSeconds < 125; }
        }

        public string StatusText
        {
            get
            {
                if (_cfg.SyncMode == "server") return ServerStatusText;
                if (_cfg.SyncMode != "lan") return "仅本机（不联网）";
                if (_cfg.SyncPaused) return "已暂停全部同步";
                SyncPeer p = Peer;
                if (p == null) return "未配对";
                if (_lastError != null && (DateTime.UtcNow - _lastErrorUtc).TotalSeconds < 90) return "同步失败：" + _lastError;
                if (_syncing) return "同步中…";
                if (p.Paused) return "这台设备已暂停收发";
                if (!PeerOnline)
                {
                    if (string.IsNullOrEmpty(p.Address) && p.LastSyncUtc == DateTime.MinValue)
                        return "已配对（跨网络的自动同步将在下一版提供）";
                    return "对方离线，内容会在联网后补传";
                }
                if (OutboxCount > 0) return "待补传 " + OutboxCount + " 条";
                return "已同步";
            }
        }

        /// <summary>自建服务器方式的状态文案：把"连不上服务器"与"对方离线"分开说。</summary>
        string ServerStatusText
        {
            get
            {
                if (!ServerConfigured) return "未配置服务器";
                if (_cfg.SyncPaused) return "已暂停全部同步";
                SyncPeer p = Peer;
                if (p == null)
                {
                    if (ServerCodeActive) return "等待另一台输入配对码（剩余 " + (ServerCodeSecondsLeft / 60) + " 分 " + (ServerCodeSecondsLeft % 60) + " 秒）";
                    if (!string.IsNullOrEmpty(_cfg.SyncServerRoomId)) return "等待另一台确认加入";
                    return "还没配对";
                }
                if (_lastError != null && (DateTime.UtcNow - _lastErrorUtc).TotalSeconds < 90) return "同步失败：" + _lastError;
                if (_syncing) return "同步中…";
                if (p.Paused) return "这台设备已暂停收发";
                if (!_serverReachable && (DateTime.UtcNow - _lastServerOkUtc).TotalSeconds > 12) return "连不上服务器，稍后自动重试";
                if (!PeerOnline)
                {
                    if (OutboxCount > 0) return "对方离线，已排队待补传 " + OutboxCount + " 条";
                    return "对方离线，内容会在联网后补传";
                }
                if (OutboxCount > 0) return "待补传 " + OutboxCount + " 条";
                return "已同步";
            }
        }

        /// <summary>同步状态点颜色语义：0 灰（未启用/未配对）、1 绿（已同步）、2 琥珀（进行中/离线/积压）、3 红（失败）。</summary>
        public int StatusLevel
        {
            get
            {
                if (_cfg.SyncMode == "server")
                {
                    if (_cfg.SyncPaused) return 0;
                    if (Peer == null) return ServerConfigured ? 2 : 0;
                    if (_lastError != null && (DateTime.UtcNow - _lastErrorUtc).TotalSeconds < 90) return 3;
                    if (_syncing) return 2;
                    if (Peer.Paused) return 0;
                    if (!_serverReachable && (DateTime.UtcNow - _lastServerOkUtc).TotalSeconds > 12) return 3;
                    if (!PeerOnline) return 2;
                    if (OutboxCount > 0) return 2;
                    return 1;
                }
                if (_cfg.SyncMode != "lan" || _cfg.SyncPaused) return 0;
                if (Peer == null) return 2;
                if (_lastError != null && (DateTime.UtcNow - _lastErrorUtc).TotalSeconds < 90) return 3;
                if (_syncing) return 2;
                if (Peer.Paused) return 0;
                if (!PeerOnline) return 2;
                if (OutboxCount > 0) return 2;
                return 1;
            }
        }

        public string LastError { get { return _lastError; } }

        // ------------------------------------------------------------------ 配对码
        public string CurrentCode
        {
            get
            {
                if (_code == null || DateTime.UtcNow > _codeExpireUtc) return "";
                return _code;
            }
        }

        public int CodeSecondsLeft
        {
            get
            {
                if (_code == null) return 0;
                double s = (_codeExpireUtc - DateTime.UtcNow).TotalSeconds;
                if (s < 0) s = 0;
                return (int)Math.Ceiling(s);
            }
        }

        public string RefreshCode()
        {
            _code = RandomCode();
            _codeExpireUtc = DateTime.UtcNow.AddMinutes(5);
            RaiseChanged();
            return _code;
        }

        static string RandomCode()
        {
            byte[] b = RandomBytes(4);
            int n = BitConverter.ToInt32(b, 0) & 0x7fffffff;
            return (n % 1000000).ToString("000000", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ 生命周期
        public void Start()
        {
            EnsureIdentity();
            if (_cfg.SyncMode == "server")
            {
                StartServerRelay();
                return;
            }
            if (_cfg.SyncMode != "lan") return;
            if (_running) return;

            try
            {
                _server = new TcpListener(IPAddress.Any, 0);
                _server.Start();
                _port = ((IPEndPoint)_server.LocalEndpoint).Port;
            }
            catch
            {
                _lastError = "无法开启接收端口，可能被防火墙拦截";
                _lastErrorUtc = DateTime.UtcNow;
                try { if (_server != null) _server.Stop(); } catch { }
                _server = null;
                return;
            }

            _running = true;
            Thread acc = new Thread(AcceptLoop);
            acc.IsBackground = true;
            acc.Name = "ClipSync.Accept";
            acc.Start();

            StartDiscovery();

            _loopThread = new Thread(Loop);
            _loopThread.IsBackground = true;
            _loopThread.Name = "ClipSync.Loop";
            _loopThread.Start();

            try
            {
                NetworkChange.NetworkAddressChanged += OnNetworkChanged;
                NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailability;
            }
            catch { }
            RaiseChanged();
        }

        public void Stop()
        {
            StopServerRelay();
            _running = false;
            try { NetworkChange.NetworkAddressChanged -= OnNetworkChanged; } catch { }
            try { NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailability; } catch { }
            try { if (_server != null) _server.Stop(); } catch { }
            _server = null;
            try { if (_udp != null) _udp.Close(); } catch { }
            _udp = null;
            try { if (_bcast != null) _bcast.Close(); } catch { }
            _bcast = null;
            lock (_lock) { _seen.Clear(); }
            RaiseChanged();
        }

        public void SetMode(string mode)
        {
            if (mode != "lan" && mode != "server") mode = "local";
            if (_cfg.SyncMode == mode)
            {
                if (mode == "lan") Start();
                else if (mode == "server") StartServerRelay();
                return;
            }
            bool wasServer = _cfg.SyncMode == "server";
            _cfg.SyncMode = mode;
            _cfg.Save();
            if (mode == "lan")
            {
                StopServerRelay();
                Start();
            }
            else if (mode == "server")
            {
                Stop();
                StartServerRelay();
            }
            else
            {
                Stop();
            }
            if (wasServer && mode != "server") StopServerRelay();
            RaiseChanged();
        }

        void OnNetworkChanged(object s, EventArgs e)
        {
            // Wi-Fi 切换 / 掉线重连：清掉旧的地址与在线状态，下一轮自动重新发现、重连
            lock (_lock) { _seen.Clear(); }
            _lastPeerSeenUtc = DateTime.MinValue;
            _dirty = true;
            RaiseChanged();
        }

        void OnNetworkAvailability(object s, NetworkAvailabilityEventArgs e)
        {
            _dirty = true;
            RaiseChanged();
        }

        // ------------------------------------------------------------------ 发现
        void StartDiscovery()
        {
            try
            {
                _udp = new UdpClient();
                _udp.ExclusiveAddressUse = false;
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                _udp.JoinMulticastGroup(IPAddress.Parse(GroupAddress));
                _udp.MulticastLoopback = true;
            }
            catch
            {
                try { if (_udp != null) _udp.Close(); } catch { }
                _udp = null;
            }

            try
            {
                _bcast = new UdpClient();
                _bcast.EnableBroadcast = true;
                _bcast.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }
            catch
            {
                try { if (_bcast != null) _bcast.Close(); } catch { }
                _bcast = null;
            }

            if (_udp == null) return;
            _discoveryThread = new Thread(DiscoveryLoop);
            _discoveryThread.IsBackground = true;
            _discoveryThread.Name = "ClipSync.Discovery";
            _discoveryThread.Start();
        }

        void DiscoveryLoop()
        {
            while (_running && _udp != null)
            {
                try
                {
                    IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = _udp.Receive(ref ep);
                    ParseAnnounce(data, ep);
                }
                catch (SocketException)
                {
                    if (!_running) break;
                    Thread.Sleep(200);
                }
                catch
                {
                    if (!_running) break;
                    Thread.Sleep(300);
                }
            }
        }

        void ParseAnnounce(byte[] data, IPEndPoint ep)
        {
            if (data == null || data.Length > 4096) return;
            string text;
            try { text = Encoding.UTF8.GetString(data); } catch { return; }
            string[] p = text.Split('|');
            if (p.Length < 7 || p[0] != "CLIPSYNC1") return;
            string id = p[2];
            if (string.IsNullOrEmpty(id) || id == DeviceId) return;
            Discovered d = new Discovered();
            d.Id = id;
            d.Name = FromB64(p[3]);
            d.Os = FromB64(p[4]);
            int port;
            if (!int.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port <= 0) return;
            d.Port = port;
            d.Address = ep.Address.ToString();
            d.SeenUtc = DateTime.UtcNow;
            if (string.IsNullOrEmpty(d.Name)) d.Name = "另一台电脑";

            bool isPeer = false;
            lock (_lock)
            {
                Discovered old;
                bool isNew = !_seen.TryGetValue(id, out old);
                _seen[id] = d;
                if (old == null || old.Address != d.Address || old.Port != d.Port) isNew = true;
                SyncPeer peer = null;
                for (int i = 0; i < _cfg.SyncPeers.Count; i++)
                {
                    if (_cfg.SyncPeers[i].Id == id) { peer = _cfg.SyncPeers[i]; break; }
                }
                if (peer != null)
                {
                    peer.Address = d.Address;
                    peer.Port = d.Port;
                    peer.Name = d.Name;
                    peer.Os = d.Os;
                    isPeer = true;
                    _lastPeerSeenUtc = DateTime.UtcNow;
                    _dirty = true;
                }
                if (isNew || isPeer) RaiseChanged();
            }
        }

        void Announce()
        {
            if (_port <= 0) return;
            string pkt = "CLIPSYNC1|1|" + DeviceId + "|" + B64(DeviceName) + "|" + B64(OsText)
                + "|" + _port + "|" + _cfg.SyncMode + "|" + (_cfg.SyncPaused ? "1" : "0");
            byte[] data = Encoding.UTF8.GetBytes(pkt);
            if (_udp != null)
            {
                try { _udp.Send(data, data.Length, new IPEndPoint(IPAddress.Parse(GroupAddress), DiscoveryPort)); } catch { }
            }
            if (_bcast != null)
            {
                try { _bcast.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort)); } catch { }
            }
        }

        public List<Discovered> SnapshotDiscovered()
        {
            List<Discovered> list = new List<Discovered>();
            lock (_lock)
            {
                foreach (KeyValuePair<string, Discovered> kv in _seen)
                {
                    if (kv.Key == _cfg.SyncDeviceId) continue;
                    if (FindPeerLocked(kv.Key) != null) continue;
                    if ((DateTime.UtcNow - kv.Value.SeenUtc).TotalSeconds > 15) continue;
                    list.Add(kv.Value);
                }
            }
            return list.OrderBy(x => x.Name).ToList();
        }

        public List<PendingPair> SnapshotPending()
        {
            lock (_lock) { return new List<PendingPair>(_pending); }
        }

        public void AllowPending(string id)
        {
            if (_cfg.SyncMode == "server")
            {
                ThreadPool.QueueUserWorkItem(delegate { DecideServerPending(id, true); });
                return;
            }
            lock (_lock)
            {
                foreach (PendingPair p in _pending)
                {
                    if (p.Id == id) { p.Allow = true; p.Decided.Set(); return; }
                }
            }
        }

        public void DenyPending(string id)
        {
            if (_cfg.SyncMode == "server")
            {
                ThreadPool.QueueUserWorkItem(delegate { DecideServerPending(id, false); });
                return;
            }
            lock (_lock)
            {
                foreach (PendingPair p in _pending)
                {
                    if (p.Id == id) { p.Allow = false; p.Decided.Set(); return; }
                }
            }
        }

        // ------------------------------------------------------------------ 配对：本机请求加入
        /// <summary>用对方显示的 6 位配对码加入。返回 null 表示成功，否则是给用户看的失败原因。</summary>
        public string PairWith(string deviceId, string code)
        {
            if (_cfg.SyncMode != "lan") return "当前是「仅本机」，请先切换到「局域网配对」。";
            if (_cfg.SyncPeers.Count >= MaxPeers + 1) return "局域网直连已达上限（本机 + 1 台）；要多台互同步，请切到「自建服务器」（房间最多 4 台）。";
            if (code == null) code = "";
            code = code.Trim();
            if (code.Length != 6) return "请输入对方电脑上显示的 6 位配对码。";

            Discovered d = FindDiscovered(deviceId);
            if (d == null) return "这台电脑已经不在附近了，请点「刷新」后再试。";

            TcpClient c = Connect(d.Address, d.Port, 4000);
            if (c == null) return "连不上对方电脑。请确认两台在同一 Wi-Fi / 同一网段，且没有开「访客网络隔离」。";
            try
            {
                c.ReceiveTimeout = 130000;
                NetworkStream s = c.GetStream();
                Msg req = new Msg("pairreq");
                req.AddRaw("id", DeviceId);
                req.Add("name", DeviceName);
                req.Add("os", OsText);
                req.AddRaw("code", code);
                req.AddRaw("port", _port.ToString(CultureInfo.InvariantCulture));
                WriteFrame(s, req.ToBytes());

                byte[] raw = ReadFrame(s);
                if (raw == null) return "对方没有响应，请稍后再试。";
                Msg head = Msg.Parse(raw);
                if (head != null && (head.Type == "pairdeny" || head.Type == "pairyerr"))
                {
                    string reason = head.Raw("reason");
                    if (reason == "limit") return "对方的局域网直连已达上限（本机 + 1 台），需要先在对方电脑上移除一台；多台互同步请用「自建服务器」。";
                    if (reason == "local") return "对方当前是「仅本机」，还没有打开局域网配对。";
                    return "配对未被允许。";
                }

                Cipher cipher = new Cipher(DeriveFromCode(code));
                byte[] plain = cipher.Open(raw);
                if (plain == null) return "配对码不正确，或对方已经刷新了配对码。";
                Msg ok = Msg.Parse(plain);
                if (ok == null || ok.Type != "pairapprove") return "配对失败，请重试。";
                byte[] master;
                try { master = Convert.FromBase64String(ok.Raw("key")); }
                catch { return "配对失败：密钥格式异常。"; }
                int port;
                int.TryParse(ok.Raw("port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
                SavePeer(ok.Raw("id"), ok.Get("name"), ok.Get("os"), d.Address, port > 0 ? port : d.Port, master);
                return null;
            }
            catch (Exception)
            {
                return "配对失败，请检查防火墙是否拦截了这个程序。";
            }
            finally
            {
                try { c.Close(); } catch { }
            }
        }

        /// <summary>不输配对码，直接向附近电脑发出加入请求，由对方点「允许加入」确认。</summary>
        public string RequestJoin(string deviceId)
        {
            if (_cfg.SyncMode != "lan") return "当前是「仅本机」，请先切换到「局域网配对」。";
            if (_cfg.SyncPeers.Count >= MaxPeers + 1) return "局域网直连已达上限（本机 + 1 台）；要多台互同步，请切到「自建服务器」（房间最多 4 台）。";
            Discovered d = FindDiscovered(deviceId);
            if (d == null) return "这台电脑已经不在附近了，请点「刷新」后再试。";

            string code = RandomCode();
            _joinSentUtc = DateTime.UtcNow;
            RaiseChanged();
            TcpClient c = Connect(d.Address, d.Port, 4000);
            if (c == null)
            {
                _joinSentUtc = DateTime.MinValue;
                return "连不上对方电脑。请确认两台在同一 Wi-Fi / 同一网段。";
            }
            try
            {
                c.ReceiveTimeout = 130000;
                NetworkStream s = c.GetStream();
                Msg req = new Msg("pairreq");
                req.AddRaw("id", DeviceId);
                req.Add("name", DeviceName);
                req.Add("os", OsText);
                req.AddRaw("code", code);
                req.AddRaw("port", _port.ToString(CultureInfo.InvariantCulture));
                WriteFrame(s, req.ToBytes());

                byte[] raw = ReadFrame(s);
                if (raw == null) return "对方没有响应，请稍后再试。";
                Msg head = Msg.Parse(raw);
                if (head != null && (head.Type == "pairdeny" || head.Type == "pairyerr"))
                {
                    string reason = head.Raw("reason");
                    if (reason == "limit") return "对方的局域网直连已达上限（本机 + 1 台），需要先在对方电脑上移除一台；多台互同步请用「自建服务器」。";
                    if (reason == "local") return "对方当前是「仅本机」，还没有打开局域网配对。";
                    return "配对未被允许。";
                }
                Cipher cipher = new Cipher(DeriveFromCode(code));
                byte[] plain = cipher.Open(raw);
                if (plain == null) return "对方返回的确认信息无法解开，请重试。";
                Msg ok = Msg.Parse(plain);
                if (ok == null || ok.Type != "pairapprove") return "配对失败，请重试。";
                byte[] master;
                try { master = Convert.FromBase64String(ok.Raw("key")); }
                catch { return "配对失败：密钥格式异常。"; }
                int port;
                int.TryParse(ok.Raw("port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
                SavePeer(ok.Raw("id"), ok.Get("name"), ok.Get("os"), d.Address, port > 0 ? port : d.Port, master);
                return null;
            }
            catch (Exception)
            {
                return "配对失败，请检查防火墙是否拦截了这个程序。";
            }
            finally
            {
                _joinSentUtc = DateTime.MinValue;
                try { c.Close(); } catch { }
            }
        }

        // 对端接收加入请求
        void HandlePairRequest(TcpClient client, NetworkStream s, Msg m)
        {
            string peerId = m.Raw("id");
            if (string.IsNullOrEmpty(peerId) || peerId == DeviceId) return;
            if (_cfg.SyncMode != "lan")
            {
                WriteFrame(s, new Msg("pairdeny").AddRaw("reason", "local").ToBytes());
                return;
            }
            if (_cfg.SyncPeers.Count >= 1 && FindPeerLocked(peerId) == null)
            {
                WriteFrame(s, new Msg("pairyerr").AddRaw("reason", "limit").ToBytes());
                return;
            }

            string code = m.Raw("code");
            PendingPair p = new PendingPair();
            p.Id = peerId;
            p.Name = m.Get("name");
            if (string.IsNullOrEmpty(p.Name)) p.Name = "另一台电脑";
            p.Os = m.Get("os");
            p.Address = RemoteAddress(client);
            p.Code = code;
            p.Client = client;

            bool autoApprove = _code != null && DateTime.UtcNow <= _codeExpireUtc && code == _code;

            try
            {
                if (autoApprove)
                {
                    p.Allow = true;
                    p.Decided.Set();
                }
                else
                {
                    lock (_lock) { _pending.Add(p); }
                    RaisePairRequested(p.Name);
                    RaiseChanged();
                    p.Decided.WaitOne(120000);
                }
                lock (_lock) { _pending.Remove(p); }

                if (p.Allow)
                {
                    int port;
                    int.TryParse(m.Raw("port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
                    byte[] master = RandomBytes(32);
                    Cipher cipher = new Cipher(DeriveFromCode(code));
                    Msg ok = new Msg("pairapprove");
                    ok.AddRaw("id", DeviceId);
                    ok.Add("name", DeviceName);
                    ok.Add("os", OsText);
                    ok.Add("key", Convert.ToBase64String(master));
                    ok.AddRaw("port", _port.ToString(CultureInfo.InvariantCulture));
                    WriteFrame(s, cipher.Seal(ok.ToBytes()));
                    SavePeer(p.Id, p.Name, p.Os, p.Address, port, master);
                }
                else
                {
                    WriteFrame(s, new Msg("pairdeny").AddRaw("reason", "denied").ToBytes());
                }
            }
            catch { }
            finally
            {
                lock (_lock) { _pending.Remove(p); }
                RaiseChanged();
            }
        }

        void SavePeer(string id, string name, string os, string address, int port, byte[] key)
        {
            if (string.IsNullOrEmpty(id) || key == null || key.Length != 32) return;
            lock (_lock)
            {
                _cfg.SyncPeers.Clear();
                SyncPeer p = new SyncPeer();
                p.Id = id;
                p.Name = string.IsNullOrEmpty(name) ? "另一台电脑" : name;
                p.Os = os;
                p.Address = address;
                p.Port = port;
                p.Key = Convert.ToBase64String(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
                p.LastSyncUtc = DateTime.MinValue;
                _cfg.SyncPeers.Add(p);
                _ciphers.Clear();
                _lastSyncUtc = DateTime.MinValue;
                _lastError = null;
                _lastPeerSeenUtc = DateTime.UtcNow;
                _dirty = true;
            }
            _cfg.Save();
            RaiseChanged();
        }

        public void RemovePeer(string id)
        {
            bool wasServer = _cfg.SyncMode == "server";
            string removedId = id;
            lock (_lock)
            {
                if (string.IsNullOrEmpty(removedId) && _cfg.SyncPeers.Count > 0) removedId = _cfg.SyncPeers[0].Id;
                for (int i = _cfg.SyncPeers.Count - 1; i >= 0; i--)
                {
                    if (string.IsNullOrEmpty(id) || _cfg.SyncPeers[i].Id == id) _cfg.SyncPeers.RemoveAt(i);
                }
                _ciphers.Clear();
                _lastSyncUtc = DateTime.MinValue;
                _lastPeerSeenUtc = DateTime.MinValue;
                _pending.Clear();
            }
            _cfg.Save();
            if (wasServer)
            {
                ServerBeforePeerRemoved(removedId);
                ClearServerRoom(false);
            }
            RaiseChanged();
        }

        /// <summary>断开全部设备 / 更换配对密钥：作废双方共享密钥，必须重新配对。本地历史不受影响。</summary>
        public void DisconnectAll()
        {
            RemovePeer(null);
            _code = null;
            _codeExpireUtc = DateTime.MinValue;
        }

        public void SetPeerPaused(string id, bool paused)
        {
            SyncPeer p = Peer;
            if (p == null) return;
            p.Paused = paused;
            _cfg.Save();
            _dirty = true;
            RaiseChanged();
        }

        public void SetAllPaused(bool paused)
        {
            _cfg.SyncPaused = paused;
            _cfg.Save();
            _dirty = true;
            RaiseChanged();
        }

        // ------------------------------------------------------------------ 后台循环
        void Loop()
        {
            DateTime lastAnnounce = DateTime.MinValue;
            while (_running)
            {
                try
                {
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastAnnounce).TotalMilliseconds > 2000)
                    {
                        Announce();
                        lastAnnounce = now;
                    }
                    lock (_lock)
                    {
                        List<string> stale = new List<string>();
                        foreach (KeyValuePair<string, Discovered> kv in _seen)
                        {
                            if ((now - kv.Value.SeenUtc).TotalSeconds > 30) stale.Add(kv.Key);
                        }
                        foreach (string k in stale) _seen.Remove(k);
                    }

                    // 共享文件夹配对：等对方写回执（3 秒一扫，避免频繁碰网盘目录）
                    if (_folderPending != null && (now - _lastFolderScanUtc).TotalSeconds > 3)
                    {
                        _lastFolderScanUtc = now;
                        ScanFolderAck();
                    }

                    SyncPeer peer = Peer;
                    if (peer != null && !peer.Paused && !_cfg.SyncPaused)
                    {
                        bool due = _dirty || (now - _lastSyncUtc).TotalSeconds > 5;
                        bool online = (now - _lastPeerSeenUtc).TotalSeconds < 12;
                        if (due && online) SyncOnce(peer);
                    }
                }
                catch { }
                Thread.Sleep(900);
            }
        }

        void SyncOnce(SyncPeer peer)
        {
            if (_syncing) return;
            string addr;
            int port;
            if (!ResolvePeer(peer, out addr, out port)) return;

            _syncing = true;
            TcpClient c = null;
            try
            {
                c = Connect(addr, port, 3000);
                if (c == null)
                {
                    SetError("连不上对方电脑");
                    return;
                }
                c.ReceiveTimeout = 6000;
                NetworkStream s = c.GetStream();
                Cipher cipher = PeerCipher(peer);
                if (cipher == null) return;
                DateTime started = DateTime.UtcNow;   // 用轮次开始时间做游标，避免漏掉本轮中途新增的内容

                Msg hello = new Msg("hello");
                hello.AddRaw("id", DeviceId);
                hello.Add("name", DeviceName);
                hello.Add("os", OsText);
                hello.AddRaw("since", peer.LastSyncUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                WriteFrame(s, hello.ToBytes());

                SendDelta(s, cipher, peer);

                // 读对方这一轮推来的增量，直到对方发 done；先收齐再一次性并入本地，避免界面频繁刷新
                List<Msg> batch = new List<Msg>();
                while (true)
                {
                    byte[] raw = ReadFrame(s);
                    if (raw == null) break;
                    byte[] plain = cipher.Open(raw);
                    if (plain == null) break;
                    Msg m = Msg.Parse(plain);
                    if (m == null) break;
                    if (m.Type == "item" || m.Type == "tomb") { batch.Add(m); continue; }
                    if (m.Type == "bye") break;
                    if (m.Type == "done") break;
                }
                ApplyBatch(batch);

                DateTime now = DateTime.UtcNow;
                _lastSyncUtc = started;
                peer.LastSyncUtc = started;
                _lastPeerSeenUtc = now;
                _lastError = null;
                _dirty = false;
                SaveThrottled();
                RaiseChanged();
            }
            catch (Exception)
            {
                SetError("网络中断，稍后自动重试");
            }
            finally
            {
                _syncing = false;
                try { if (c != null) c.Close(); } catch { }
            }
        }

        void SendDelta(Stream s, Cipher cipher, SyncPeer peer)
        {
            List<Msg> delta = BuildDelta(peer.LastSyncUtc);
            for (int i = 0; i < delta.Count; i++) WriteEncrypted(s, cipher, delta[i]);
            WriteEncrypted(s, cipher, new Msg("done"));
        }

        /// <summary>把 since 之后的所有变更整理成消息列表（局域网与自建服务器共用同一套增量口径）。</summary>
        List<Msg> BuildDelta(DateTime since)
        {
            List<Msg> list = new List<Msg>();
            foreach (ClipItem it in SnapshotItems())
            {
                if (it.Sensitive) continue;   // 疑似密码类内容永远不跨设备
                if (since == DateTime.MinValue || it.Modified.ToUniversalTime() > since)
                {
                    list.Add(ItemMsg(it));
                    // 记下这次告诉对方的次数，对方回传同一条时差值为 0，避免次数被翻倍累加
                    SetCountMark(it.Hash, it.Count);
                }
            }
            foreach (SyncTombstone t in _cfg.SyncDeleted.ToList())
            {
                if (since == DateTime.MinValue || t.DeletedUtc > since)
                {
                    Msg m = new Msg("tomb");
                    m.AddRaw("hash", t.Hash);
                    m.AddRaw("at", t.DeletedUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                    list.Add(m);
                }
            }
            return list;
        }

        Msg ItemMsg(ClipItem it)
        {
            Msg m = new Msg("item");
            m.AddRaw("id", it.Id);
            m.AddRaw("hash", it.Hash);
            m.AddRaw("kind", it.Kind);
            m.Add("text", it.Text == null ? "" : it.Text);
            m.Add("app", it.App == null ? "" : it.App);
            m.AddRaw("color", it.AppColor == null ? "" : it.AppColor);
            m.AddRaw("created", it.Created.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            m.AddRaw("count", it.Count.ToString(CultureInfo.InvariantCulture));
            m.AddRaw("pin", it.Pinned ? "1" : "0");
            m.AddRaw("fav", it.Fav ? "1" : "0");
            m.Add("src", DeviceName);
            if (it.IsImage)
            {
                m.AddRaw("w", it.W.ToString(CultureInfo.InvariantCulture));
                m.AddRaw("h", it.H.ToString(CultureInfo.InvariantCulture));
                m.AddRaw("bytes", it.Bytes.ToString(CultureInfo.InvariantCulture));
                m.Add("fmt", it.Fmt == null ? "" : it.Fmt);
            }
            return m;
        }

        void ApplyBatch(List<Msg> batch)
        {
            if (batch == null || batch.Count == 0) return;
            if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
            {
                try
                {
                    Application.Current.Dispatcher.Invoke(new Action(delegate { ApplyBatchCore(batch); }));
                    return;
                }
                catch { }
            }
            ApplyBatchCore(batch);
        }

        void ApplyBatchCore(List<Msg> batch)
        {
            SyncPeer peer = Peer;
            _applying = true;
            try
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    Msg m = batch[i];
                    if (m.Type == "item") ApplyItem(m, peer);
                    else if (m.Type == "tomb") ApplyTombstone(m);
                }
            }
            catch { }
            finally { _applying = false; }
            _dirty = false;
        }

        List<ClipItem> SnapshotItems()
        {
            List<ClipItem> list = new List<ClipItem>();
            try
            {
                for (int i = 0; i < _store.Items.Count; i++) list.Add(_store.Items[i]);
            }
            catch { }
            return list;
        }

        void ApplyItem(Msg m, SyncPeer peer)
        {
            string hash = m.Raw("hash");
            if (string.IsNullOrEmpty(hash)) return;
            if (_store.IsBlocked(hash)) return;
            if (IsTombstoned(hash)) return;

            string kind = m.Raw("kind");
            bool isImage = kind == "image";
            long ticks = m.Long("created", 0);
            DateTime created = ticks > 0
                ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime()
                : DateTime.Now;
            int remoteCount = m.Int("count", 1);
            if (remoteCount < 1) remoteCount = 1;
            bool pin = m.Raw("pin") == "1";
            bool fav = m.Raw("fav") == "1";

            _applying = true;
            ClipItem local = _store.FindByHash(hash);
            if (local == null)
            {
                ClipItem it = new ClipItem();
                it.Id = Guid.NewGuid().ToString("N");
                it.Hash = hash;
                it.Kind = isImage ? "image" : "text";
                it.Text = isImage ? null : m.Get("text");
                it.App = m.Get("app");
                it.AppColor = m.Raw("color");
                if (string.IsNullOrEmpty(it.AppColor)) it.AppColor = AppResolver.ColorOf(it.App);
                it.Created = created;
                it.Modified = DateTime.Now;
                it.Count = remoteCount;
                it.Pinned = pin;
                it.Fav = fav;
                if (isImage)
                {
                    it.W = m.Int("w", 0);
                    it.H = m.Int("h", 0);
                    it.Bytes = m.Long("bytes", 0);
                    it.Fmt = m.Get("fmt");
                }
                it.SourceDevice = m.Get("src");
                it.RemoteId = m.Raw("id");
                _store.IngestRemote(it);
                SetCountMark(hash, remoteCount);
            }
            else
            {
                int applied = GetCountMark(hash);
                int delta = remoteCount - applied;
                bool changed = false;
                if (delta > 0)
                {
                    local.Count += delta;
                    changed = true;
                }
                if (remoteCount > applied) SetCountMark(hash, remoteCount);
                if (created > local.Created)
                {
                    local.Created = created;
                    changed = true;
                }
                if (pin && !local.Pinned) { local.Pinned = true; changed = true; }
                if (fav && !local.Fav) { local.Fav = true; changed = true; }
                if (changed)
                {
                    local.Modified = DateTime.Now;
                    _store.NotifyChanged();
                }
            }
        }

        void ApplyTombstone(Msg m)
        {
            string hash = m.Raw("hash");
            if (string.IsNullOrEmpty(hash)) return;
            if (!IsTombstoned(hash)) AddTombstone(hash, false);
            try { _store.RemoveByHash(hash); }
            catch { }
        }

        int GetCountMark(string hash)
        {
            lock (_countLock)
            {
                for (int i = 0; i < _cfg.SyncCounts.Count; i++)
                {
                    if (_cfg.SyncCounts[i].Hash == hash) return _cfg.SyncCounts[i].Count;
                }
            }
            return 0;
        }

        void SetCountMark(string hash, int count)
        {
            lock (_countLock)
            {
                for (int i = 0; i < _cfg.SyncCounts.Count; i++)
                {
                    if (_cfg.SyncCounts[i].Hash == hash)
                    {
                        _cfg.SyncCounts[i].Count = count;
                        return;
                    }
                }
                SyncCountMark mark = new SyncCountMark();
                mark.Hash = hash;
                mark.Count = count;
                _cfg.SyncCounts.Add(mark);
                if (_cfg.SyncCounts.Count > 2000) _cfg.SyncCounts.RemoveAt(0);
            }
        }

        bool IsTombstoned(string hash)
        {
            if (_cfg.SyncDeleted == null) return false;
            DateTime cutoff = DateTime.UtcNow.AddDays(-30);
            for (int i = 0; i < _cfg.SyncDeleted.Count; i++)
            {
                SyncTombstone t = _cfg.SyncDeleted[i];
                if (t.Hash == hash) return t.DeletedUtc > cutoff;
            }
            return false;
        }

        void AddTombstone(string hash, bool save)
        {
            if (string.IsNullOrEmpty(hash)) return;
            AddTombstoneInternal(hash, save);
        }

        void AddTombstoneInternal(string hash, bool save)
        {
            for (int i = 0; i < _cfg.SyncDeleted.Count; i++)
            {
                if (_cfg.SyncDeleted[i].Hash == hash)
                {
                    _cfg.SyncDeleted[i].DeletedUtc = DateTime.UtcNow;
                    if (save) _cfg.Save();
                    return;
                }
            }
            SyncTombstone t = new SyncTombstone();
            t.Hash = hash;
            t.DeletedUtc = DateTime.UtcNow;
            _cfg.SyncDeleted.Add(t);
            if (_cfg.SyncDeleted.Count > 1000) _cfg.SyncDeleted.RemoveAt(0);
            if (save) _cfg.Save();
        }

        /// <summary>本机历史有变化（新增/改标记/合并），下一轮把增量推给对方。</summary>
        public void NotifyStoreChanged()
        {
            if (_applying) return;
            _dirty = true;
        }

        public void NotifyLocalDeleted(ClipItem it)
        {
            if (it == null || _applying || string.IsNullOrEmpty(it.Hash)) return;
            if (Peer == null) return;                             // 没配对就不留删除标记
            if (_cfg.SyncMode != "lan" && _cfg.SyncMode != "server") return;
            AddTombstone(it.Hash, true);
            RemoveCountMark(it.Hash);
            _dirty = true;
            RaiseChanged();
        }

        public void NotifyLocalRestored(ClipItem it)
        {
            if (it == null || string.IsNullOrEmpty(it.Hash)) return;
            for (int i = _cfg.SyncDeleted.Count - 1; i >= 0; i--)
            {
                if (_cfg.SyncDeleted[i].Hash == it.Hash) _cfg.SyncDeleted.RemoveAt(i);
            }
            _cfg.Save();
            _dirty = true;
            RaiseChanged();
        }

        void RemoveCountMark(string hash)
        {
            lock (_countLock)
            {
                for (int i = _cfg.SyncCounts.Count - 1; i >= 0; i--)
                {
                    if (_cfg.SyncCounts[i].Hash == hash) _cfg.SyncCounts.RemoveAt(i);
                }
            }
        }

        // ------------------------------------------------------------------ 接收
        void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _server.AcceptTcpClient();
                }
                catch
                {
                    if (!_running) break;
                    Thread.Sleep(200);
                    continue;
                }
                TcpClient captured = client;
                ThreadPool.QueueUserWorkItem(ServeClient, captured);
            }
        }

        void ServeClient(object state)
        {
            TcpClient client = state as TcpClient;
            if (client == null) return;
            try
            {
                client.ReceiveTimeout = 8000;
                NetworkStream s = client.GetStream();
                byte[] first = ReadFrame(s);
                if (first == null) return;
                Msg head = Msg.Parse(first);
                if (head == null) return;

                if (head.Type == "pairreq")
                {
                    HandlePairRequest(client, s, head);
                    return;
                }
                if (head.Type != "hello") return;

                string peerId = head.Raw("id");
                SyncPeer peer = FindPeerLocked(peerId);
                if (peer == null || _cfg.SyncMode != "lan" || _cfg.SyncPaused || peer.Paused)
                {
                    byte[] bye = Encoding.UTF8.GetBytes("bye|reason=unpaired");
                    WriteFrame(s, bye);
                    return;
                }
                Cipher cipher = PeerCipher(peer);
                if (cipher == null) return;

                DateTime since = new DateTime(head.Long("since", 0), DateTimeKind.Utc);
                DateTime started = DateTime.UtcNow;
                bool sent = false;
                bool gotDone = false;
                List<Msg> batch = new List<Msg>();
                while (true)
                {
                    byte[] raw = ReadFrame(s);
                    if (raw == null) break;
                    byte[] plain = cipher.Open(raw);
                    if (plain == null) break;
                    Msg m = Msg.Parse(plain);
                    if (m == null) break;

                    if (m.Type == "item" || m.Type == "tomb") { batch.Add(m); }
                    else if (m.Type == "reqimg") { SendImage(s, cipher, m); }
                    else if (m.Type == "bye") break;
                    else if (m.Type == "done")
                    {
                        gotDone = true;
                        if (!sent)
                        {
                            sent = true;
                            SendDeltaSince(s, cipher, since);
                            WriteEncrypted(s, cipher, new Msg("done"));
                        }
                        if (since == DateTime.MinValue) since = DateTime.UtcNow;
                        // 对方收完最后一条 done 会主动断开
                        break;
                    }
                }
                ApplyBatch(batch);

                if (gotDone)
                {
                    DateTime now = DateTime.UtcNow;
                    peer.LastSyncUtc = started;
                    _lastSyncUtc = started;
                    _lastPeerSeenUtc = now;
                    _lastError = null;
                    _dirty = false;
                    SaveThrottled();
                    RaiseChanged();
                }
            }
            catch { }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        /// <summary>服务端按对方给的 since 回推它的增量（和自己的 LastSync 无关）。</summary>
        void SendDeltaSince(Stream s, Cipher cipher, DateTime since)
        {
            foreach (ClipItem it in SnapshotItems())
            {
                if (it.Sensitive) continue;
                if (since == DateTime.MinValue || it.Modified.ToUniversalTime() > since)
                {
                    WriteEncrypted(s, cipher, ItemMsg(it));
                    SetCountMark(it.Hash, it.Count);
                }
            }
            foreach (SyncTombstone t in _cfg.SyncDeleted.ToList())
            {
                if (since == DateTime.MinValue || t.DeletedUtc > since) 
                {
                    Msg m = new Msg("tomb");
                    m.AddRaw("hash", t.Hash);
                    m.AddRaw("at", t.DeletedUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                    WriteEncrypted(s, cipher, m);
                }
            }
        }

        void SendImage(Stream s, Cipher cipher, Msg m)
        {
            string hash = m.Raw("hash");
            ClipItem it = _store.FindByHash(hash);
            if (it == null || !it.IsImage || !_store.HasLocalImage(it))
            {
                WriteEncrypted(s, cipher, new Msg("imgerr").AddRaw("hash", hash).AddRaw("reason", "missing"));
                return;
            }
            try
            {
                byte[] png = File.ReadAllBytes(_store.ImagePath(it));
                if (png.Length > 20 * 1024 * 1024)
                {
                    WriteEncrypted(s, cipher, new Msg("imgerr").AddRaw("hash", hash).AddRaw("reason", "toolarge"));
                    return;
                }
                Msg img = new Msg("img");
                img.AddRaw("hash", hash);
                img.Add("data", Convert.ToBase64String(png));
                WriteEncrypted(s, cipher, img);
            }
            catch
            {
                WriteEncrypted(s, cipher, new Msg("imgerr").AddRaw("hash", hash).AddRaw("reason", "io"));
            }
        }

        // ------------------------------------------------------------------ 按需取图
        /// <summary>在后台拉取远端图片原图；返回给用户看的第一句提示。</summary>
        public string RequestImage(ClipItem it)
        {
            if (it == null || !it.IsImage) return "";
            if (_store.HasLocalImage(it)) return "";
            if (_cfg.SyncMode == "server") return RequestImageServer(it);
            SyncPeer peer = Peer;
            if (_cfg.SyncMode != "lan" || peer == null) return "这台图片来自另一台电脑，当前没有配对设备，无法获取。";
            if (peer.Paused || _cfg.SyncPaused) return "同步已暂停：恢复这台设备的同步后才能获取图片。";
            if (!PeerOnline) return "对方设备当前离线，联网后会自动恢复，稍后再点一次。";
            if (_imageBusyHash == it.Hash) return "正在获取这张图片…";
            _imageBusyHash = it.Hash;

            string hash = it.Hash;
            string sizeText = TextUtil.SizeText(it.Bytes);
            string peerName = peer.Name;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = FetchImage(it, hash);
                _imageBusyHash = null;
                if (err != null)
                {
                    SetError(err);
                }
                else
                {
                    _lastError = null;
                    RaiseChanged();
                }
            });
            return "正在从 " + peerName + " 获取图片…（" + sizeText + "）";
        }

        string FetchImage(ClipItem item, string hash)
        {
            SyncPeer peer = Peer;
            if (peer == null) return "配对已断开";
            string addr;
            int port;
            if (!ResolvePeer(peer, out addr, out port)) return "连不上对方电脑";
            TcpClient c = Connect(addr, port, 3000);
            if (c == null) return "连不上对方电脑";
            try
            {
                c.ReceiveTimeout = 20000;
                NetworkStream s = c.GetStream();
                Cipher cipher = PeerCipher(peer);
                if (cipher == null) return "配对密钥不可用";

                Msg hello = new Msg("hello");
                hello.AddRaw("id", DeviceId);
                hello.Add("name", DeviceName);
                hello.Add("os", OsText);
                hello.AddRaw("since", DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                WriteFrame(s, hello.ToBytes());
                WriteEncrypted(s, cipher, new Msg("reqimg").AddRaw("hash", hash));
                WriteEncrypted(s, cipher, new Msg("done"));

                while (true)
                {
                    byte[] raw = ReadFrame(s);
                    if (raw == null) break;
                    byte[] plain = cipher.Open(raw);
                    if (plain == null) break;
                    Msg m = Msg.Parse(plain);
                    if (m == null) break;
                    if (m.Type == "img" && m.Raw("hash") == hash)
                    {
                        byte[] png;
                        try { png = Convert.FromBase64String(m.Get("data")); }
                        catch { return "收到的图片数据不完整"; }
                        if (png.Length > 20 * 1024 * 1024) return "图片太大（超过 20 MB），未获取";
                        _applying = true;
                        try
                        {
                            if (!_store.AttachImage(item, png)) return "图片保存失败";
                        }
                        finally { _applying = false; }
                        return null;
                    }
                    if (m.Type == "imgerr")
                    {
                        string reason = m.Raw("reason");
                        if (reason == "missing") return "对方已经没有这张图片了";
                        if (reason == "toolarge") return "图片太大（超过 20 MB），未获取";
                        return "对方读取图片失败";
                    }
                    if (m.Type == "bye") return "对方已断开配对";
                    if (m.Type == "done") break;
                }
                return "对方没有返回图片";
            }
            catch
            {
                return "获取图片超时，请稍后再试";
            }
            finally
            {
                try { c.Close(); } catch { }
            }
        }

        // ------------------------------------------------------------------ 共享文件夹配对（跨网络）
        public string RelayDir
        {
            get { return _cfg.SyncRelayDir == null ? "" : _cfg.SyncRelayDir; }
            set
            {
                _cfg.SyncRelayDir = value == null ? "" : value;
                _cfg.Save();
                _inviteCacheUtc = DateTime.MinValue;
                _lastFolderScanUtc = DateTime.MinValue;
                RaiseChanged();
            }
        }

        public bool FolderPairPending
        {
            get
            {
                lock (_lock)
                {
                    return _folderPending != null && DateTime.UtcNow < _folderPending.ExpireUtc;
                }
            }
        }

        public string FolderPairCode
        {
            get { lock (_lock) { return _folderPending == null ? "" : _folderPending.Code; } }
        }

        public string FolderError { get { return _folderError; } }

        string RelayRoot(bool create)
        {
            string root = _cfg.SyncRelayDir;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            string dir = Path.Combine(root, "ClipboardHelper-Sync");
            if (create)
            {
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                }
                catch { return null; }
            }
            return dir;
        }

        /// <summary>第一台：在共享文件夹里写一份配对邀请。成功返回 null，失败返回原因。</summary>
        public string CreateFolderInvite()
        {
            _folderError = null;
            if (_cfg.SyncMode != "lan") return "请先切换到「局域网配对」，否则不会开启同步。";
            if (Peer != null) return "这台电脑已经配对了一台设备，请先断开再重新配对。";
            string dir = RelayRoot(true);
            if (dir == null) return "共享文件夹不可用。请选择一个两台电脑都能读写的网盘同步文件夹。";

            string code = RandomCode();
            byte[] salt = RandomBytes(16);
            byte[] master = RandomBytes(32);
            DateTime exp = DateTime.UtcNow.AddMinutes(10);
            string payload = Convert.ToBase64String(master) + "|" + DeviceId + "|" + B64(DeviceName) + "|"
                + B64(OsText) + "|" + exp.Ticks.ToString(CultureInfo.InvariantCulture);
            byte[] blob = new Cipher(DeriveFromCode(code, salt)).Seal(Encoding.UTF8.GetBytes(payload));

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("CLIPPAIR1");
            sb.AppendLine("id=" + B64(DeviceId));
            sb.AppendLine("name=" + B64(DeviceName));
            sb.AppendLine("os=" + B64(OsText));
            sb.AppendLine("exp=" + exp.Ticks.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("salt=" + Convert.ToBase64String(salt));
            sb.AppendLine("blob=" + Convert.ToBase64String(blob));

            string path = Path.Combine(dir, "pair-" + DeviceId + ".txt");
            if (!WriteAtomic(path, sb.ToString()))
            {
                _folderError = "写入共享文件夹失败，可能被杀毒或网盘客户端占用";
                return "写入共享文件夹失败（可能被杀毒或网盘客户端占用），请稍后再试。";
            }
            lock (_lock)
            {
                _folderPending = new FolderPairing();
                _folderPending.Code = code;
                _folderPending.Master = master;
                _folderPending.ExpireUtc = exp;
                _folderPending.InvitePath = path;
                _inviteCacheUtc = DateTime.MinValue;
            }
            RaiseChanged();
            return null;
        }

        public void CancelFolderInvite()
        {
            FolderPairing p;
            lock (_lock)
            {
                p = _folderPending;
                _folderPending = null;
            }
            if (p != null && !string.IsNullOrEmpty(p.InvitePath))
            {
                try { if (File.Exists(p.InvitePath)) File.Delete(p.InvitePath); } catch { }
            }
            RaiseChanged();
        }

        /// <summary>第二台：读取邀请，用 6 位配对码解开邀请里的密钥材料，并写回执。</summary>
        public string AcceptInvite(PairInvite inv, string code)
        {
            if (inv == null) return "邀请已经不存在了，请刷新后再试。";
            if (_cfg.SyncMode != "lan") return "请先切换到「局域网配对」。";
            if (Peer != null) return "这台电脑已经配对了一台设备，请先断开再重新配对。";
            if (code == null) code = "";
            code = code.Trim();
            if (code.Length != 6) return "请输入 6 位配对码。";
            if (DateTime.UtcNow > inv.ExpireUtc) return "邀请已经过期，请让对方重新生成配对邀请。";

            byte[] plain = new Cipher(DeriveFromCode(code, inv.Salt)).Open(inv.Blob);
            if (plain == null) return "配对码不正确，或邀请已经被刷新。";
            string text = Encoding.UTF8.GetString(plain);
            string[] parts = text.Split('|');
            if (parts.Length < 5) return "邀请内容损坏，请让对方重新生成。";
            byte[] master;
            try { master = Convert.FromBase64String(parts[0]); }
            catch { return "邀请内容损坏，请让对方重新生成。"; }
            if (master.Length != 32) return "邀请内容损坏，请让对方重新生成。";
            string hostId = parts[1];
            string hostName = FromB64(parts[2]);
            string hostOs = FromB64(parts[3]);
            long expTicks;
            if (!long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out expTicks)
                || new DateTime(expTicks, DateTimeKind.Utc) < DateTime.UtcNow)
            {
                return "邀请已经过期，请让对方重新生成。";
            }

            string dir = RelayRoot(true);
            if (dir == null) return "共享文件夹不可用，无法写回执。";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("CLIPPAIRACK1");
            sb.AppendLine("to=" + B64(hostId));
            sb.AppendLine("id=" + B64(DeviceId));
            sb.AppendLine("name=" + B64(DeviceName));
            sb.AppendLine("os=" + B64(OsText));
            sb.AppendLine("proof=" + Convert.ToBase64String(Hmac(master, "ack|" + hostId + "|" + DeviceId)));
            string ackPath = Path.Combine(dir, "ack-" + hostId + "-" + DeviceId + ".txt");
            if (!WriteAtomic(ackPath, sb.ToString())) return "写回执失败，可能网盘客户端正在占用该文件夹。";

            SavePeer(hostId, hostName, hostOs, "", 0, master);
            _inviteCacheUtc = DateTime.MinValue;
            return null;
        }

        /// <summary>共享文件夹里当前有效的邀请（缓存 3 秒，避免每秒都扫网盘目录）。</summary>
        public List<PairInvite> SnapshotInvites()
        {
            string dir = RelayRoot(false);
            if (dir == null) return new List<PairInvite>();
            if ((DateTime.UtcNow - _inviteCacheUtc).TotalSeconds < 3) return new List<PairInvite>(_inviteCache);

            List<PairInvite> list = new List<PairInvite>();
            Dictionary<string, PairInvite> byId = new Dictionary<string, PairInvite>();
            try
            {
                string[] files = Directory.GetFiles(dir, "pair-*.txt");
                foreach (string f in files)
                {
                    Dictionary<string, string> kv = ReadKv(f, "CLIPPAIR1");
                    if (kv == null) continue;
                    string id = FromB64(KvGet(kv, "id"));
                    if (string.IsNullOrEmpty(id) || id == DeviceId) continue;
                    long ticks;
                    if (!long.TryParse(KvGet(kv, "exp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) continue;
                    DateTime exp = new DateTime(ticks, DateTimeKind.Utc);
                    if (exp < DateTime.UtcNow) continue;
                    byte[] salt;
                    byte[] blob;
                    try
                    {
                        salt = Convert.FromBase64String(KvGet(kv, "salt"));
                        blob = Convert.FromBase64String(KvGet(kv, "blob"));
                    }
                    catch { continue; }
                    if (salt.Length != 16 || blob.Length < 48) continue;

                    PairInvite inv = new PairInvite();
                    inv.Id = id;
                    inv.Name = FromB64(KvGet(kv, "name"));
                    if (string.IsNullOrEmpty(inv.Name)) inv.Name = "另一台电脑";
                    inv.Os = FromB64(KvGet(kv, "os"));
                    inv.ExpireUtc = exp;
                    inv.FilePath = f;
                    inv.Salt = salt;
                    inv.Blob = blob;

                    PairInvite old;
                    if (!byId.TryGetValue(id, out old) || old.ExpireUtc < exp) byId[id] = inv;
                }
            }
            catch { }
            foreach (KeyValuePair<string, PairInvite> kv in byId) list.Add(kv.Value);
            _inviteCache = list;
            _inviteCacheUtc = DateTime.UtcNow;
            return new List<PairInvite>(list);
        }

        void ScanFolderAck()
        {
            FolderPairing pending;
            lock (_lock) { pending = _folderPending; }
            if (pending == null) return;
            if (DateTime.UtcNow > pending.ExpireUtc)
            {
                lock (_lock) { _folderPending = null; }
                RaiseChanged();
                return;
            }
            string dir = RelayRoot(false);
            if (dir == null) return;
            string[] files;
            try { files = Directory.GetFiles(dir, "ack-" + DeviceId + "-*.txt"); }
            catch { return; }

            foreach (string f in files)
            {
                Dictionary<string, string> kv = ReadKv(f, "CLIPPAIRACK1");
                if (kv == null) continue;
                if (FromB64(KvGet(kv, "to")) != DeviceId) continue;
                string joinerId = FromB64(KvGet(kv, "id"));
                if (string.IsNullOrEmpty(joinerId) || joinerId == DeviceId) continue;
                byte[] proof;
                try { proof = Convert.FromBase64String(KvGet(kv, "proof")); }
                catch { continue; }
                if (!FixedEquals(proof, Hmac(pending.Master, "ack|" + DeviceId + "|" + joinerId))) continue;

                SavePeer(joinerId, FromB64(KvGet(kv, "name")), FromB64(KvGet(kv, "os")), "", 0, pending.Master);
                lock (_lock) { _folderPending = null; _inviteCacheUtc = DateTime.MinValue; }
                try { File.Delete(f); } catch { }
                try { if (!string.IsNullOrEmpty(pending.InvitePath) && File.Exists(pending.InvitePath)) File.Delete(pending.InvitePath); } catch { }
                RaiseChanged();
                return;
            }
        }

        public void RetryNow()
        {
            _lastError = null;
            _dirty = true;
            _lastSyncUtc = DateTime.MinValue;
            _inviteCacheUtc = DateTime.MinValue;
            RaiseChanged();
        }

        static byte[] Hmac(byte[] key, string text)
        {
            using (HMACSHA256 h = new HMACSHA256(key))
            {
                return h.ComputeHash(Encoding.UTF8.GetBytes(text));
            }
        }

        static bool FixedEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        static bool WriteAtomic(string path, string text)
        {
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try { File.Delete(path); }
                    catch
                    {
                        // 网盘客户端可能短暂占用，改用带序号的文件名（对方按内容去重）
                        tmp = path + "." + DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture) + ".txt";
                        File.WriteAllText(tmp, text, new UTF8Encoding(false));
                        return true;
                    }
                }
                File.Move(tmp, path);
                return true;
            }
            catch { return false; }
        }

        static Dictionary<string, string> ReadKv(string path, string head)
        {
            try
            {
                using (StreamReader r = new StreamReader(path, Encoding.UTF8, true))
                {
                    string first = r.ReadLine();
                    if (first == null || first.Trim() != head) return null;
                    Dictionary<string, string> kv = new Dictionary<string, string>();
                    string line;
                    int guard = 0;
                    while ((line = r.ReadLine()) != null && guard++ < 40)
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        kv[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                    return kv;
                }
            }
            catch { return null; }
        }

        static string KvGet(Dictionary<string, string> kv, string key)
        {
            string v;
            return kv.TryGetValue(key, out v) ? v : "";
        }

        // ------------------------------------------------------------------ 网络工具
        bool ResolvePeer(SyncPeer peer, out string address, out int port)
        {
            address = peer.Address;
            port = peer.Port;
            lock (_lock)
            {
                Discovered d;
                if (_seen.TryGetValue(peer.Id, out d) && (DateTime.UtcNow - d.SeenUtc).TotalSeconds < 15)
                {
                    address = d.Address;
                    port = d.Port;
                }
            }
            return !string.IsNullOrEmpty(address) && port > 0;
        }

        static TcpClient Connect(string host, int port, int timeoutMs)
        {
            TcpClient c = new TcpClient();
            try
            {
                IAsyncResult ar = c.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    try { c.Close(); } catch { }
                    return null;
                }
                c.EndConnect(ar);
                return c;
            }
            catch
            {
                try { c.Close(); } catch { }
                return null;
            }
        }

        static string RemoteAddress(TcpClient c)
        {
            try
            {
                IPEndPoint ep = c.Client.RemoteEndPoint as IPEndPoint;
                if (ep != null) return ep.Address.ToString();
            }
            catch { }
            return "";
        }

        void WriteEncrypted(Stream s, Cipher cipher, Msg m)
        {
            WriteFrame(s, cipher.Seal(m.ToBytes()));
        }

        void SetError(string err)
        {
            _lastError = err;
            _lastErrorUtc = DateTime.UtcNow;
            RaiseChanged();
        }

        void SaveThrottled()
        {
            if ((DateTime.UtcNow - _lastSaveUtc).TotalSeconds < 8) return;
            _lastSaveUtc = DateTime.UtcNow;
            _cfg.Save();
        }

        void RaiseChanged()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastUiUtc).TotalMilliseconds < 250) return;
            _lastUiUtc = now;
            try
            {
                if (Application.Current == null) return;
                Application.Current.Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (Changed != null) Changed();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }
        }

        void RaisePairRequested(string deviceName)
        {
            try
            {
                if (Application.Current == null) return;
                Application.Current.Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (PairRequested != null) PairRequested(deviceName);
                }), System.Windows.Threading.DispatcherPriority.Normal);
            }
            catch { }
        }

        // ------------------------------------------------------------------ 加密
        static byte[] RandomBytes(int n)
        {
            byte[] b = new byte[n];
            RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
            rng.GetBytes(b);
            rng.Dispose();
            return b;
        }

        static byte[] DeriveFromCode(string code)
        {
            return DeriveFromCode(code, Encoding.UTF8.GetBytes("ClipboardHelper-LAN-Pair-v1"));
        }

        static byte[] DeriveFromCode(string code, byte[] salt)
        {
            Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(code, salt, 20000);
            byte[] key = kdf.GetBytes(32);
            kdf.Reset();
            return key;
        }

        Cipher PeerCipher(SyncPeer peer)
        {
            if (peer == null || string.IsNullOrEmpty(peer.Key)) return null;
            lock (_lock)
            {
                Cipher cached;
                if (_ciphers.TryGetValue(peer.Id, out cached)) return cached;
                try
                {
                    byte[] master = ProtectedData.Unprotect(Convert.FromBase64String(peer.Key), null, DataProtectionScope.CurrentUser);
                    if (master == null || master.Length != 32) return null;
                    Cipher c = new Cipher(master);
                    _ciphers[peer.Id] = c;
                    return c;
                }
                catch { return null; }
            }
        }

        SyncPeer FindPeerLocked(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < _cfg.SyncPeers.Count; i++)
            {
                if (_cfg.SyncPeers[i].Id == id) return _cfg.SyncPeers[i];
            }
            return null;
        }

        Discovered FindDiscovered(string id)
        {
            lock (_lock)
            {
                Discovered d;
                if (_seen.TryGetValue(id, out d) && (DateTime.UtcNow - d.SeenUtc).TotalSeconds < 15) return d;
            }
            return null;
        }

        static string B64(string s)
        {
            if (s == null) s = "";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        }

        static string FromB64(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
            catch { return ""; }
        }

        // ------------------------------------------------------------------ 帧
        static void WriteFrame(Stream s, byte[] payload)
        {
            if (payload == null) return;
            byte[] h = new byte[4];
            int n = payload.Length;
            h[0] = (byte)((n >> 24) & 0xFF);
            h[1] = (byte)((n >> 16) & 0xFF);
            h[2] = (byte)((n >> 8) & 0xFF);
            h[3] = (byte)(n & 0xFF);
            s.Write(h, 0, 4);
            s.Write(payload, 0, n);
            s.Flush();
        }

        static byte[] ReadFrame(Stream s)
        {
            byte[] h = new byte[4];
            if (!ReadExact(s, h, 4)) return null;
            int n = (h[0] << 24) | (h[1] << 16) | (h[2] << 8) | h[3];
            if (n <= 0 || n > MaxFrame) return null;
            byte[] b = new byte[n];
            if (!ReadExact(s, b, n)) return null;
            return b;
        }

        static bool ReadExact(Stream s, byte[] buf, int len)
        {
            int off = 0;
            while (off < len)
            {
                int n;
                try { n = s.Read(buf, off, len - off); }
                catch { return false; }
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        // ------------------------------------------------------------------ 消息
        class Msg
        {
            public string Type = "";
            readonly List<string[]> _fields = new List<string[]>();

            public Msg(string type)
            {
                Type = type;
            }

            public Msg Add(string key, string value)
            {
                _fields.Add(new string[] { key, Convert.ToBase64String(Encoding.UTF8.GetBytes(value == null ? "" : value)) });
                return this;
            }

            public Msg AddRaw(string key, string value)
            {
                _fields.Add(new string[] { key, value == null ? "" : value.Replace("|", "").Replace("\r", "").Replace("\n", "") });
                return this;
            }

            public byte[] ToBytes()
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(Type);
                for (int i = 0; i < _fields.Count; i++)
                {
                    sb.Append('|');
                    sb.Append(_fields[i][0]);
                    sb.Append('=');
                    sb.Append(_fields[i][1]);
                }
                return Encoding.UTF8.GetBytes(sb.ToString());
            }

            public static Msg Parse(byte[] data)
            {
                if (data == null) return null;
                string text;
                try { text = Encoding.UTF8.GetString(data); } catch { return null; }
                string[] parts = text.Split('|');
                if (parts.Length == 0 || parts[0].Length == 0) return null;
                Msg m = new Msg(parts[0]);
                for (int i = 1; i < parts.Length; i++)
                {
                    int eq = parts[i].IndexOf('=');
                    if (eq <= 0) continue;
                    m._fields.Add(new string[] { parts[i].Substring(0, eq), parts[i].Substring(eq + 1) });
                }
                return m;
            }

            public string Raw(string key)
            {
                for (int i = 0; i < _fields.Count; i++)
                {
                    if (_fields[i][0] == key) return _fields[i][1];
                }
                return "";
            }

            public string Get(string key)
            {
                string raw = Raw(key);
                if (raw.Length == 0) return "";
                try { return Encoding.UTF8.GetString(Convert.FromBase64String(raw)); }
                catch { return ""; }
            }

            public int Int(string key, int def)
            {
                int n;
                if (int.TryParse(Raw(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
                return def;
            }

            public long Long(string key, long def)
            {
                long n;
                if (long.TryParse(Raw(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
                return def;
            }
        }

        class Cipher
        {
            readonly byte[] _encKey;
            readonly byte[] _macKey;

            public Cipher(byte[] master)
            {
                _encKey = Sha256(Concat(master, Encoding.UTF8.GetBytes("enc")));
                _macKey = Sha256(Concat(master, Encoding.UTF8.GetBytes("mac")));
            }

            public byte[] Seal(byte[] plain)
            {
                byte[] iv = RandomBytes(16);
                byte[] cipher;
                using (Aes aes = Aes.Create())
                {
                    aes.Key = _encKey;
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    using (ICryptoTransform t = aes.CreateEncryptor())
                    {
                        cipher = t.TransformFinalBlock(plain, 0, plain.Length);
                    }
                }
                byte[] macInput = Concat(iv, cipher);
                byte[] mac;
                using (HMACSHA256 h = new HMACSHA256(_macKey))
                {
                    mac = h.ComputeHash(macInput);
                }
                return Concat(macInput, mac);
            }

            public byte[] Open(byte[] data)
            {
                if (data == null || data.Length < 16 + 16 + 32) return null;
                int macLen = 32;
                byte[] macInput = new byte[data.Length - macLen];
                Array.Copy(data, 0, macInput, 0, macInput.Length);
                byte[] mac = new byte[macLen];
                Array.Copy(data, macInput.Length, mac, 0, macLen);

                byte[] expect;
                using (HMACSHA256 h = new HMACSHA256(_macKey))
                {
                    expect = h.ComputeHash(macInput);
                }
                if (!FixedEquals(mac, expect)) return null;

                byte[] iv = new byte[16];
                Array.Copy(macInput, 0, iv, 0, 16);
                byte[] cipher = new byte[macInput.Length - 16];
                Array.Copy(macInput, 16, cipher, 0, cipher.Length);
                try
                {
                    using (Aes aes = Aes.Create())
                    {
                        aes.Key = _encKey;
                        aes.IV = iv;
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;
                        using (ICryptoTransform t = aes.CreateDecryptor())
                        {
                            return t.TransformFinalBlock(cipher, 0, cipher.Length);
                        }
                    }
                }
                catch { return null; }
            }

            static bool FixedEquals(byte[] a, byte[] b)
            {
                if (a == null || b == null || a.Length != b.Length) return false;
                int diff = 0;
                for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
                return diff == 0;
            }

            static byte[] Sha256(byte[] data)
            {
                using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(data);
            }

            static byte[] Concat(byte[] a, byte[] b)
            {
                byte[] r = new byte[a.Length + b.Length];
                Array.Copy(a, 0, r, 0, a.Length);
                Array.Copy(b, 0, r, a.Length, b.Length);
                return r;
            }
        }
    }
}
