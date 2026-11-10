// 剪贴板助手 · 自建同步服务器（密文中转）
//
// 设计边界（写死，不越界）：
//   - 只做密文中转：客户端把已经端到端加密好的帧（AES-256-CBC + HMAC-SHA256）发上来，
//     服务端原样存盘、原样转发。服务端不持有明文，也不持有任何解密密钥。
//   - 落盘内容是客户端给的密文（base64 文本），与客户端本地加密结果完全一致。
//   - 日志只记录连接（IP / 方法 / 路径 / 状态码 / 错误码），绝不记录请求体与任何内容。
//   - 不引入数据库，全部用文件；过期数据自动回收。
//
// 依赖：只用 Go 标准库。编译：CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -trimpath -ldflags "-s -w" .
package main

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
)

const (
	version        = "1.0.0"
	maxRoomDevices = 2 // 一间房最多 2 台设备（首发口径）
	maxFrameSize   = 24 * 1024 * 1024
	maxPushCount   = 64
	maxPullCount   = 100
	maxPullBytes   = 48 * 1024 * 1024
	codeTTL        = 5 * time.Minute
)

// ---------------------------------------------------------------- 数据结构

type Device struct {
	ID        string    `json:"id"`
	Name      string    `json:"name"`
	OS        string    `json:"os"`
	TokenHash string    `json:"tokenHash"`
	Approved  bool      `json:"approved"`
	Joined    time.Time `json:"joined"`
	LastSeen  time.Time `json:"lastSeen"`
}

type Pending struct {
	ID        string    `json:"id"`
	Name      string    `json:"name"`
	OS        string    `json:"os"`
	TokenHash string    `json:"tokenHash"`
	At        time.Time `json:"at"`
}

type Room struct {
	ID         string     `json:"id"`
	Code       string     `json:"code"`
	CodeExpire time.Time  `json:"codeExpire"`
	Created    time.Time  `json:"created"`
	LastActive time.Time  `json:"lastActive"`
	Seq        int64      `json:"seq"`
	Devices    []*Device  `json:"devices"`
	Pendings   []*Pending `json:"pendings"`

	mu sync.Mutex `json:"-"`
}

type Config struct {
	Port     int
	Data     string
	TTL      time.Duration
	TLSCert  string
	TLSKey   string
	MaxFrame int64
}

type Server struct {
	cfg     Config
	mapMu   sync.RWMutex
	rooms   map[string]*Room
	limiter *limiter
}

// ---------------------------------------------------------------- 启动

func main() {
	cfg := loadConfig()

	s, err := newServer(cfg)
	if err != nil {
		log.Fatalf("启动失败：%v", err)
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/v1/health", s.handleHealth)
	mux.HandleFunc("/v1/room/open", s.handleRoomOpen)
	mux.HandleFunc("/v1/room/join", s.handleRoomJoin) // POST 加入 / GET 查询状态
	mux.HandleFunc("/v1/room/pending", s.handlePending)
	mux.HandleFunc("/v1/room/approve", s.handleApprove)
	mux.HandleFunc("/v1/room/deny", s.handleDeny)
	mux.HandleFunc("/v1/room/devices", s.handleDevices)
	mux.HandleFunc("/v1/room/revoke", s.handleRevoke)
	mux.HandleFunc("/v1/room/verify", s.handleVerifyCode)
	mux.HandleFunc("/v1/push", s.handlePush)
	mux.HandleFunc("/v1/pull", s.handlePull)
	mux.HandleFunc("/v1/ack", s.handleAck)
	mux.HandleFunc("/", s.handleRoot)

	handler := s.withLog(mux)
	srv := &http.Server{
		Addr:              ":" + strconv.Itoa(cfg.Port),
		Handler:           handler,
		ReadHeaderTimeout: 10 * time.Second,
		ReadTimeout:       60 * time.Second,
		WriteTimeout:      90 * time.Second,
		IdleTimeout:       120 * time.Second,
	}

	go s.cleanupLoop()

	log.Printf("剪贴板助手同步服务 %s 已启动", version)
	log.Printf("监听端口：%d   数据目录：%s", cfg.Port, cfg.Data)
	log.Printf("中转数据保留：%s（两端确认后立即删除）", cfg.TTL)
	if cfg.TLSCert != "" && cfg.TLSKey != "" {
		log.Printf("已启用 HTTPS（证书：%s）", cfg.TLSCert)
	} else {
		log.Printf("当前为 HTTP。内容本身已端到端加密，TLS 只是额外一层；需要更隐蔽时按文档配 HTTPS。")
	}
	log.Printf("日志只记录连接与错误码，不记录任何内容。")

	done := make(chan os.Signal, 1)
	signal.Notify(done, os.Interrupt, syscall.SIGTERM)
	go func() {
		<-done
		log.Printf("收到退出信号，正在关闭…")
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		srv.Shutdown(ctx)
	}()

	if cfg.TLSCert != "" && cfg.TLSKey != "" {
		err = srv.ListenAndServeTLS(cfg.TLSCert, cfg.TLSKey)
	} else {
		err = srv.ListenAndServe()
	}
	if err != nil && err != http.ErrServerClosed {
		log.Fatalf("服务退出：%v", err)
	}
}

func loadConfig() Config {
	portDef := envInt("CLIP_PORT", 47820)
	ttlDef := envInt("CLIP_TTL_HOURS", 168) // 7 天
	dataDef := envStr("CLIP_DATA", "./data")

	port := flag.Int("port", portDef, "监听端口")
	data := flag.String("data", dataDef, "数据目录（只存密文）")
	ttl := flag.Int("ttl-hours", ttlDef, "中转数据保留小时数")
	cert := flag.String("tls-cert", envStr("CLIP_TLS_CERT", ""), "HTTPS 证书路径（可选）")
	key := flag.String("tls-key", envStr("CLIP_TLS_KEY", ""), "HTTPS 私钥路径（可选）")
	flag.Parse()

	if *ttl < 1 {
		*ttl = 1
	}
	return Config{
		Port:     *port,
		Data:     *data,
		TTL:      time.Duration(*ttl) * time.Hour,
		TLSCert:  *cert,
		TLSKey:   *key,
		MaxFrame: maxFrameSize,
	}
}

func envStr(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}

func envInt(k string, def int) int {
	if v := os.Getenv(k); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			return n
		}
	}
	return def
}

func newServer(cfg Config) (*Server, error) {
	if err := os.MkdirAll(filepath.Join(cfg.Data, "rooms"), 0o700); err != nil {
		return nil, err
	}
	s := &Server{
		cfg:     cfg,
		rooms:   make(map[string]*Room),
		limiter: newLimiter(),
	}
	s.loadRooms()
	return s, nil
}

func (s *Server) loadRooms() {
	root := filepath.Join(s.cfg.Data, "rooms")
	entries, err := os.ReadDir(root)
	if err != nil {
		return
	}
	for _, e := range entries {
		if !e.IsDir() {
			continue
		}
		path := filepath.Join(root, e.Name(), "room.json")
		raw, err := os.ReadFile(path)
		if err != nil {
			continue
		}
		var r Room
		if json.Unmarshal(raw, &r) != nil || r.ID == "" {
			continue
		}
		s.rooms[r.ID] = &r
	}
	log.Printf("已载入 %d 个配对房间", len(s.rooms))
}

// ---------------------------------------------------------------- 房间持久化

func (s *Server) roomDir(id string) string {
	return filepath.Join(s.cfg.Data, "rooms", id)
}

func (s *Server) inboxDir(roomID, deviceID string) string {
	return filepath.Join(s.roomDir(roomID), "inbox", sanitizeID(deviceID))
}

func (s *Server) persist(r *Room) {
	dir := s.roomDir(r.ID)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return
	}
	raw, err := json.Marshal(r)
	if err != nil {
		return
	}
	tmp := filepath.Join(dir, "room.json.tmp")
	if err := os.WriteFile(tmp, raw, 0o600); err != nil {
		return
	}
	os.Rename(tmp, filepath.Join(dir, "room.json"))
}

func (s *Server) getRoom(id string) *Room {
	if id == "" {
		return nil
	}
	s.mapMu.RLock()
	defer s.mapMu.RUnlock()
	return s.rooms[id]
}

func (s *Server) putRoom(r *Room) {
	s.mapMu.Lock()
	s.rooms[r.ID] = r
	s.mapMu.Unlock()
}

func (s *Server) dropRoom(id string) {
	s.mapMu.Lock()
	delete(s.rooms, id)
	s.mapMu.Unlock()
	os.RemoveAll(s.roomDir(id))
}

// ---------------------------------------------------------------- 鉴权

type reqCtx struct {
	code   string
	status int
}

// authDevice 校验 roomId + deviceId + token 是否为本房间里已批准的设备。
func (s *Server) authDevice(r *Room, deviceID, token string) (*Device, string) {
	if r == nil {
		return nil, "no_room"
	}
	h := hashToken(token)
	for _, d := range r.Devices {
		if d.ID != deviceID {
			continue
		}
		if !d.Approved {
			return nil, "revoked"
		}
		if !sameHash(d.TokenHash, h) {
			return nil, "bad_token"
		}
		return d, ""
	}
	return nil, "revoked"
}

func hashToken(t string) string {
	sum := sha256.Sum256([]byte(t))
	return hex.EncodeToString(sum[:])
}

func sameHash(a, b string) bool {
	return subtle.ConstantTimeCompare([]byte(a), []byte(b)) == 1
}

// ---------------------------------------------------------------- 失败次数限制

type ipRec struct {
	fails      int
	windowFrom time.Time
	bannedTill time.Time
}

type limiter struct {
	mu  sync.Mutex
	ips map[string]*ipRec
}

func newLimiter() *limiter {
	return &limiter{ips: make(map[string]*ipRec)}
}

// check 返回（是否允许，还要等多少秒）
func (l *limiter) check(ip string) (bool, int) {
	l.mu.Lock()
	defer l.mu.Unlock()
	rec := l.ips[ip]
	if rec == nil {
		return true, 0
	}
	now := time.Now()
	if now.Before(rec.bannedTill) {
		return false, int(rec.bannedTill.Sub(now).Seconds()) + 1
	}
	if now.Sub(rec.windowFrom) > 10*time.Minute {
		rec.fails = 0
		rec.windowFrom = now
	}
	return true, 0
}

func (l *limiter) fail(ip string) {
	l.mu.Lock()
	defer l.mu.Unlock()
	rec := l.ips[ip]
	if rec == nil {
		rec = &ipRec{windowFrom: time.Now()}
		l.ips[ip] = rec
	}
	rec.fails++
	if rec.fails >= 6 {
		rec.bannedTill = time.Now().Add(10 * time.Minute)
		rec.fails = 0
	}
}

func (l *limiter) ok(ip string) {
	l.mu.Lock()
	defer l.mu.Unlock()
	if rec := l.ips[ip]; rec != nil {
		rec.fails = 0
	}
}

func (l *limiter) sweep() {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := time.Now()
	for k, rec := range l.ips {
		if now.After(rec.bannedTill) && now.Sub(rec.windowFrom) > 30*time.Minute {
			delete(l.ips, k)
		}
	}
}

// ---------------------------------------------------------------- 响应与日志

func writeJSON(w http.ResponseWriter, status int, v interface{}) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("X-Clip-Relay", "1")
	w.WriteHeader(status)
	json.NewEncoder(w).Encode(v)
}

func writeErr(w http.ResponseWriter, status int, code, msg string) {
	writeJSON(w, status, map[string]string{"code": code, "message": msg})
}

type logWriter struct {
	http.ResponseWriter
	status int
	code   string
}

func (l *logWriter) WriteHeader(n int) {
	l.status = n
	l.ResponseWriter.WriteHeader(n)
}

func (l *logWriter) Write(b []byte) (int, error) {
	if l.status == 0 {
		l.status = 200
	}
	return l.ResponseWriter.Write(b)
}

func (s *Server) withLog(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		lw := &logWriter{ResponseWriter: w}
		start := time.Now()
		next.ServeHTTP(lw, r)
		if lw.status == 0 {
			lw.status = 200
		}
		// 只记录连接与结果，绝不记录请求体、内容或配对码
		log.Printf("%s %s %s -> %d (%dms)", clientIP(r), r.Method, r.URL.Path, lw.status, time.Since(start).Milliseconds())
	})
}

func clientIP(r *http.Request) string {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
	return host
}

// ---------------------------------------------------------------- 通用解析

func decodeBody(r *http.Request, v interface{}) error {
	body, err := io.ReadAll(io.LimitReader(r.Body, 48*1024*1024))
	if err != nil {
		return err
	}
	if len(body) == 0 {
		return fmt.Errorf("empty")
	}
	return json.Unmarshal(body, v)
}

func sanitizeID(id string) string {
	if len(id) < 8 || len(id) > 64 {
		return ""
	}
	for _, c := range id {
		if (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || c == '-' {
			continue
		}
		return ""
	}
	return strings.ToLower(id)
}

func cleanText(s string, max int) string {
	s = strings.TrimSpace(strings.ReplaceAll(strings.ReplaceAll(s, "\r", " "), "\n", " "))
	s = strings.Map(func(r rune) rune {
		if r < 32 {
			return -1
		}
		return r
	}, s)
	if len([]rune(s)) > max {
		s = string([]rune(s)[:max])
	}
	return s
}

func randHex(n int) string {
	b := make([]byte, n)
	if _, err := rand.Read(b); err != nil {
		// 极端情况下退化成时间派生的值，仍然保证不重复
		copy(b, []byte(strconv.FormatInt(time.Now().UnixNano(), 16)))
	}
	return hex.EncodeToString(b)
}

func randCode() string {
	b := make([]byte, 4)
	rand.Read(b)
	n := int(b[0])<<16 | int(b[1])<<8 | int(b[2])
	return fmt.Sprintf("%06d", n%1000000)
}

// ---------------------------------------------------------------- 接口：健康检查

func (s *Server) handleRoot(w http.ResponseWriter, r *http.Request) {
	writeErr(w, http.StatusNotFound, "not_found", "这是剪贴板助手的同步服务，请用客户端连接。")
}

func (s *Server) handleHealth(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, map[string]interface{}{
		"ok":      true,
		"service": "clipsync-relay",
		"version": version,
		"time":    time.Now().UTC().Format(time.RFC3339),
		"rooms":   s.roomCount(),
	})
}

func (s *Server) roomCount() int {
	s.mapMu.RLock()
	defer s.mapMu.RUnlock()
	return len(s.rooms)
}

// ---------------------------------------------------------------- 接口：配对

// POST /v1/room/open 第一台：开一间房并拿到 6 位配对码
func (s *Server) handleRoomOpen(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeErr(w, http.StatusMethodNotAllowed, "method", "只支持 POST")
		return
	}
	var in struct {
		DeviceID string `json:"deviceId"`
		Name     string `json:"name"`
		OS       string `json:"os"`
		Token    string `json:"token"`
	}
	if err := decodeBody(r, &in); err != nil {
		writeErr(w, http.StatusBadRequest, "bad_body", "请求格式不对")
		return
	}
	devID := sanitizeID(in.DeviceID)
	if devID == "" || len(in.Token) < 32 {
		writeErr(w, http.StatusBadRequest, "bad_device", "设备信息不完整")
		return
	}
	now := time.Now().UTC()
	room := &Room{
		ID:         randHex(16),
		Code:       s.uniqueCode(),
		CodeExpire: now.Add(codeTTL),
		Created:    now,
		LastActive: now,
		Devices: []*Device{{
			ID: devID, Name: cleanText(in.Name, 40), OS: cleanText(in.OS, 40),
			TokenHash: hashToken(in.Token), Approved: true, Joined: now, LastSeen: now,
		}},
	}
	s.putRoom(room)
	room.mu.Lock()
	s.persist(room)
	room.mu.Unlock()

	log.Printf("新建配对房间 %s（等待另一台输入配对码）", room.ID)
	writeJSON(w, http.StatusOK, map[string]interface{}{
		"roomId":    room.ID,
		"code":      room.Code,
		"expireSec": int(codeTTL.Seconds()),
	})
}

func (s *Server) uniqueCode() string {
	for i := 0; i < 50; i++ {
		c := randCode()
		dup := false
		s.mapMu.RLock()
		for _, r := range s.rooms {
			if r.Code == c && time.Now().UTC().Before(r.CodeExpire) {
				dup = true
				break
			}
		}
		s.mapMu.RUnlock()
		if !dup {
			return c
		}
	}
	return randCode()
}

// POST /v1/room/join 第二台：用配对码申请加入
// GET  /v1/room/join 第二台：查询自己的申请是否被允许
func (s *Server) handleRoomJoin(w http.ResponseWriter, r *http.Request) {
	if r.Method == http.MethodGet {
		s.handleJoinState(w, r)
		return
	}
	if r.Method != http.MethodPost {
		writeErr(w, http.StatusMethodNotAllowed, "method", "只支持 POST")
		return
	}
	ip := clientIP(r)
	if ok, wait := s.limiter.check(ip); !ok {
		writeJSON(w, http.StatusTooManyRequests, map[string]interface{}{
			"code": "banned", "message": "尝试次数过多，请稍后再试", "retryAfter": wait,
		})
		return
	}

	var in struct {
		Code     string `json:"code"`
		DeviceID string `json:"deviceId"`
		Name     string `json:"name"`
		OS       string `json:"os"`
		Token    string `json:"token"`
	}
	if err := decodeBody(r, &in); err != nil {
		writeErr(w, http.StatusBadRequest, "bad_body", "请求格式不对")
		return
	}
	devID := sanitizeID(in.DeviceID)
	if devID == "" || len(in.Token) < 32 {
		writeErr(w, http.StatusBadRequest, "bad_device", "设备信息不完整")
		return
	}
	code := strings.TrimSpace(in.Code)
	if len(code) != 6 {
		s.limiter.fail(ip)
		writeErr(w, http.StatusNotFound, "bad_code", "配对码不正确或已过期")
		return
	}

	room := s.findRoomByCode(code)
	if room == nil {
		s.limiter.fail(ip)
		writeErr(w, http.StatusNotFound, "bad_code", "配对码不正确或已过期")
		return
	}

	room.mu.Lock()
	defer room.mu.Unlock()
	now := time.Now().UTC()
	room.LastActive = now

	// 同一设备重复加入：token 一致视为同一条连接（多端连接复用），不一致视为换了安装
	for _, d := range room.Devices {
		if d.ID == devID {
			if sameHash(d.TokenHash, hashToken(in.Token)) {
				d.LastSeen = now
				s.persist(room)
				s.limiter.ok(ip)
				writeJSON(w, http.StatusOK, map[string]interface{}{"roomId": room.ID, "state": "approved"})
				return
			}
			s.limiter.fail(ip)
			writeErr(w, http.StatusConflict, "device_conflict", "这台设备已经在配对里了。要重新配对，请先在另一端移除它。")
			return
		}
	}
	approved := 0
	for _, d := range room.Devices {
		if d.Approved {
			approved++
		}
	}
	if approved >= maxRoomDevices {
		s.limiter.fail(ip)
		writeErr(w, http.StatusConflict, "room_full", "对方已达上限（最多 2 台），需要先在那台电脑上移除一台。")
		return
	}
	// 同一设备在多个网络上同时请求：更新而不是新增
	exists := false
	for _, p := range room.Pendings {
		if p.ID == devID {
			p.Name = cleanText(in.Name, 40)
			p.OS = cleanText(in.OS, 40)
			p.TokenHash = hashToken(in.Token)
			p.At = now
			exists = true
		}
	}
	if !exists {
		room.Pendings = append(room.Pendings, &Pending{
			ID: devID, Name: cleanText(in.Name, 40), OS: cleanText(in.OS, 40),
			TokenHash: hashToken(in.Token), At: now,
		})
	}
	s.persist(room)
	s.limiter.ok(ip)
	log.Printf("房间 %s 收到加入申请", room.ID)
	writeJSON(w, http.StatusOK, map[string]interface{}{"roomId": room.ID, "state": "pending"})
}

func (s *Server) findRoomByCode(code string) *Room {
	now := time.Now().UTC()
	s.mapMu.RLock()
	defer s.mapMu.RUnlock()
	for _, r := range s.rooms {
		if r.Code == code && now.Before(r.CodeExpire) {
			return r
		}
	}
	return nil
}

func (s *Server) handleJoinState(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	room := s.getRoom(q.Get("roomId"))
	if room == nil {
		writeErr(w, http.StatusNotFound, "no_room", "配对已失效，请重新配对")
		return
	}
	devID := sanitizeID(q.Get("deviceId"))
	token := q.Get("token")
	room.mu.Lock()
	defer room.mu.Unlock()
	for _, d := range room.Devices {
		if d.ID == devID && sameHash(d.TokenHash, hashToken(token)) {
			d.LastSeen = time.Now().UTC()
			s.persist(room)
			writeJSON(w, http.StatusOK, map[string]interface{}{"roomId": room.ID, "state": "approved"})
			return
		}
	}
	for _, p := range room.Pendings {
		if p.ID == devID && sameHash(p.TokenHash, hashToken(token)) {
			writeJSON(w, http.StatusOK, map[string]interface{}{"roomId": room.ID, "state": "pending"})
			return
		}
	}
	writeJSON(w, http.StatusOK, map[string]interface{}{"roomId": room.ID, "state": "denied"})
}

// GET /v1/room/pending 第一台：看有哪些设备在等确认
func (s *Server) handlePending(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	room := s.getRoom(q.Get("roomId"))
	if room == nil {
		writeErr(w, http.StatusNotFound, "no_room", "配对已失效，请重新配对")
		return
	}
	room.mu.Lock()
	defer room.mu.Unlock()
	if _, code := s.authDevice(room, sanitizeID(q.Get("deviceId")), q.Get("token")); code != "" {
		writeErr(w, http.StatusForbidden, code, "没有权限查看")
		return
	}
	list := make([]map[string]interface{}, 0, len(room.Pendings))
	for _, p := range room.Pendings {
		list = append(list, map[string]interface{}{
			"deviceId": p.ID, "name": p.Name, "os": p.OS, "at": p.At.Format(time.RFC3339),
		})
	}
	writeJSON(w, http.StatusOK, map[string]interface{}{"pending": list})
}

// POST /v1/room/approve 第一台：允许加入
func (s *Server) handleApprove(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeErr(w, http.StatusMethodNotAllowed, "method", "只支持 POST")
		return
	}
	var in struct {
		RoomID   string `json:"roomId"`
		DeviceID string `json:"deviceId"`
		Token    string `json:"token"`
		Target   string `json:"target"`
	}
	if err := decodeBody(r, &in); err != nil {
		writeErr(w, http.StatusBadRequest, "bad_body", "请求格式不对")
		return
	}
	room := s.getRoom(in.RoomID)
	if room == nil {
		writeErr(w, http.StatusNotFound, "no_room", "配对已失效，请重新配对")
		return
	}
	room.mu.Lock()
	defer room.mu.Unlock()
	if _, code := s.authDevice(room, sanitizeID(in.DeviceID), in.Token); code != "" {
		writeErr(w, http.StatusForbidden, code, "没有权限操作")
		return
	}
	approved := 0
	for _, d := range room.Devices {
		if d.Approved {
			approved++
		}
	}
	if approved >= maxRoomDevices {
		writeErr(w, http.StatusConflict, "room_full", "已达上限（最多 2 台），请先移除一台")
		return
	}
	var target *Pending
	rest := make([]*Pending, 0, len(room.Pendings))
	for _, p := range room.Pendings {
		if p.ID == sanitizeID(in.Target) {
			target = p
		} else {
			rest = append(rest, p)
		}
	}
	if target == nil {
		writeErr(w, http.StatusNotFound, "no_pending", "这台设备已经不在等待列表里了")
		return
	}
	room.Pendings = rest
	now := time.Now().UTC()
	room.Devices = append(room.Devices, &Device{
		ID: target.ID, Name: target.Name, OS: target.OS,
		TokenHash: target.TokenHash, Approved: true, Joined: now, LastSeen: now,
	})
	// 配对码一次性，用完立即作废
	room.Code = ""
	room.CodeExpire = time.Time{}
	room.LastActive = now
	s.persist(room)
	log.Printf("房间 %s 已允许新设备加入", room.ID)
	writeJSON(w, http.StatusOK, map[string]interface{}{"ok": true, "device": target.ID})
}

// POST /v1/room/deny 第一台：拒绝
func (s *Server) handleDeny(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeErr(w, http.StatusMethodNotAllowed, "method", "只支持 POST")
		return
	}
	var in struct {
		RoomID   string `json:"roomId"`
		DeviceID string `json:"deviceId"`
		Token    string `json:"token"`
		Target   string `json:"target"`
	}
	if err := decodeBody(r, &in); err != nil {
		writeErr(w, http.StatusBadRequest, "bad_body", "请求格式不对")
		return
	}
	room := s.getRoom(in.RoomID)
	if room == nil {
		writeErr(w, http.StatusNotFound, "no_room", "配对已失效，请重新配对")
		return
	}
	room.mu.Lock()
	defer room.mu.Unlock()
	if _, code := s.authDevice(room, sanitizeID(in.DeviceID), in.Token); code != "" {
		writeErr(w, http.StatusForbidden, code, "没有权限操作")
		return
	}
	rest := make([]*Pending, 0, len(room.Pendings))
	for _, p := range room.Pendings {
		if p.ID != sanitizeID(in.Target) {
			rest = append(rest, p)
		}
	}
	room.Pendings = rest
	s.persist(room)
	writeJSON(w, http.StatusOK, map[string]interface{}{"ok": true})
}

// POST /v1/room/verify 连通测试时顺带校验配对码
func (s *Server) handleVerifyCode(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeErr(w, http.StatusMethodNotAllowed, "method", "只支持 POST")
		return
	}
	ip := clientIP(r)
	if ok, wait := s.limiter.check(ip); !ok {
		writeJSON(w, http.StatusTooManyRequests, map[string]interface{}{
			"code": "banned", "message": "尝试次数过多，请稍后再试", "retryAfter": wait,
		})
		return
	}
	var in struct {
		Code string `json:"code"`
	}
	if err := decodeBody(r, &in); err != nil {
		writeErr(w, http.StatusBadRequest, "bad_body", "请求格式不对")
		return
	}
	if s.findRoomByCode(strings.TrimSpace(in.Code)) == nil {
		s.limiter.fail(ip)
		writeErr(w, http.StatusNotFound, "bad_code", "配对码不正确或已过期")
		return
	}
	s.limiter.ok(ip)
	writeJSON(w, http.StatusOK, map[string]interface{}{"ok": true})
}

// GET /v1/room/devices 已配对设备与在线状态
func (s *Server) handleDevices(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	room := s.getRoom(q.Get("roomId"))
	if room == nil {
		writeErr(w, http.StatusNotFound, "no_room", "配对已失效，请重新配对")
		return
	}
	me := sanitizeID(q.Get("deviceId"))
	room.mu.Lock()
	defer room.mu.Unlock()
	if _, code := s.authDevice(room, me, q.Get("token")); code != "" {
		writeErr(w, http.StatusForbidden, code, "没有权限查看")
		return
	}
	now := time.Now().UTC()
	list := make([]map[string]interface{}, 0, len(room.Devices))
	for _, d := range room.Devices {
		if !d.Approved {
			continue
		}
		list = append(list, map[string]interface{}{
			"deviceId":    d.ID,
			"name":        d.Name,
			"os":          d.OS,
			"self":        d.ID == me,
			"online":      now.Sub(d.LastSeen) < 20*time.Second,
			"lastSeenSec": int(now.Sub(d.LastSeen).Seconds()),
		})
	}
	writeJSON(w, http.StatusOK, map[string]interface{}{"devices": list, "limit": maxRoomDevices})
}

// POST /v1/room/revoke 吊销一台设备（也可用于自己被移除）
func (s *Server) handleRevoke(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeErr(w, http.StatusMethodNotAllowed, "method", "只支持 POST")
		return
	}
	var in struct {
		RoomID   string `json:"roomId"`
		DeviceID string `json:"deviceId"`
		Token    string `json:"token"`
		Target   string `json:"target"`
	}
	if err := decodeBody(r, &in); err != nil {
		writeErr(w, http.StatusBadRequest, "bad_body", "请求格式不对")
		return
	}
	room := s.getRoom(in.RoomID)
	if room == nil {
		writeErr(w, http.StatusNotFound, "no_room", "配对已失效，请重新配对")
		return
	}
	room.mu.Lock()
	defer room.mu.Unlock()
	if _, code := s.authDevice(room, sanitizeID(in.DeviceID), in.Token); code != "" {
		writeErr(w, http.StatusForbidden, code, "没有权限操作")
		return
	}
	target := sanitizeID(in.Target)
	if target == "" {
		writeErr(w, http.StatusBadRequest, "bad_device", "缺少要移除的设备")
		return
	}
	rest := make([]*Device, 0, len(room.Devices))
	for _, d := range room.Devices {
		if d.ID == target {
			// 吊销 = 从白名单删除 + 销毁它的收件箱；它之后发来的请求一律拒绝
			continue
		}
		rest = append(rest, d)
	}
	room.Devices = rest
	pending := make([]*Pending, 0, len(room.Pendings))
	for _, p := range room.Pendings {
		if p.ID != target {
			pending = append(pending, p)
		}
	}
	room.Pendings = pending
	room.LastActive = time.Now().UTC()
	s.persist(room)
	os.RemoveAll(s.inboxDir(room.ID, target))
	log.Printf("房间 %s 已吊销一台设备", room.ID)
	writeJSON(w, http.StatusOK, map[string]interface{}{"ok": true})
}

// ---------------------------------------------------------------- 接口：中转

// POST /v1/push 推送一批密文帧
func (s *Server) handlePush(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeErr(w, http.StatusMethodNotAllowed, "method", "只支持 POST")
		return
	}
	var in struct {
		RoomID   string   `json:"roomId"`
		DeviceID string   `json:"deviceId"`
		Token    string   `json:"token"`
		Frames   []string `json:"frames"`
	}
	if err := decodeBody(r, &in); err != nil {
		writeErr(w, http.StatusBadRequest, "bad_body", "请求格式不对")
		return
	}
	room := s.getRoom(in.RoomID)
	if room == nil {
		writeErr(w, http.StatusNotFound, "no_room", "配对已失效，请重新配对")
		return
	}
	me := sanitizeID(in.DeviceID)
	room.mu.Lock()
	defer room.mu.Unlock()
	if _, code := s.authDevice(room, me, in.Token); code != "" {
		writeErr(w, http.StatusForbidden, code, "没有权限推送")
		return
	}
	if len(in.Frames) == 0 {
		writeJSON(w, http.StatusOK, map[string]interface{}{"seq": room.Seq})
		return
	}
	if len(in.Frames) > maxPushCount {
		writeErr(w, http.StatusBadRequest, "too_many", "一批最多 64 条")
		return
	}
	now := time.Now().UTC()
	written := 0
	for _, f := range in.Frames {
		data := strings.TrimSpace(f)
		if data == "" {
			continue
		}
		raw, err := base64.StdEncoding.DecodeString(data)
		if err != nil {
			writeErr(w, http.StatusBadRequest, "bad_frame", "帧格式不对（只接受 base64 密文）")
			return
		}
		if int64(len(raw)) > s.cfg.MaxFrame {
			writeErr(w, http.StatusRequestEntityTooLarge, "too_large", "单条内容超过大小上限")
			return
		}
		room.Seq++
		name := fmt.Sprintf("%016d.env", room.Seq)
		for _, d := range room.Devices {
			if !d.Approved || d.ID == me {
				continue
			}
			dir := s.inboxDir(room.ID, d.ID)
			if os.MkdirAll(dir, 0o700) != nil {
				continue
			}
			// 原样落盘客户端给的密文，服务端不做任何解密
			os.WriteFile(filepath.Join(dir, name), []byte(data), 0o600)
		}
		written++
	}
	room.LastActive = now
	for _, d := range room.Devices {
		if d.ID == me {
			d.LastSeen = now
		}
	}
	s.persist(room)
	writeJSON(w, http.StatusOK, map[string]interface{}{"seq": room.Seq, "accepted": written})
}

// GET /v1/pull 长轮询拉取对方推来的密文帧
func (s *Server) handlePull(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	room := s.getRoom(q.Get("roomId"))
	if room == nil {
		writeErr(w, http.StatusNotFound, "no_room", "配对已失效，请重新配对")
		return
	}
	me := sanitizeID(q.Get("deviceId"))
	room.mu.Lock()
	dev, code := s.authDevice(room, me, q.Get("token"))
	if code != "" {
		room.mu.Unlock()
		writeErr(w, http.StatusForbidden, code, "没有权限拉取")
		return
	}
	dev.LastSeen = time.Now().UTC()
	since, _ := strconv.ParseInt(q.Get("since"), 10, 64)
	waitSec, _ := strconv.Atoi(q.Get("wait"))
	if waitSec < 0 {
		waitSec = 0
	}
	if waitSec > 25 {
		waitSec = 25
	}
	room.mu.Unlock()

	deadline := time.Now().Add(time.Duration(waitSec) * time.Second)
	for {
		frames, next := s.readInbox(room.ID, me, since)
		if len(frames) > 0 || time.Now().After(deadline) {
			room.mu.Lock()
			peerOnline, peerSeen := false, -1
			for _, d := range room.Devices {
				if d.Approved && d.ID != me {
					sec := int(time.Since(d.LastSeen).Seconds())
					peerSeen = sec
					peerOnline = sec < 20
				}
			}
			revoked := true
			for _, d := range room.Devices {
				if d.ID == me && d.Approved {
					revoked = false
				}
			}
			room.mu.Unlock()
			writeJSON(w, http.StatusOK, map[string]interface{}{
				"frames":      frames,
				"next":        next,
				"peerOnline":  peerOnline,
				"peerSeenSec": peerSeen,
				"revoked":     revoked,
			})
			return
		}
		select {
		case <-r.Context().Done():
			return
		case <-time.After(250 * time.Millisecond):
		}
	}
}

func (s *Server) readInbox(roomID, deviceID string, since int64) ([]map[string]interface{}, int64) {
	dir := s.inboxDir(roomID, deviceID)
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil, since
	}
	type item struct {
		seq  int64
		name string
	}
	items := make([]item, 0, len(entries))
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".env") {
			continue
		}
		n, err := strconv.ParseInt(strings.TrimSuffix(e.Name(), ".env"), 10, 64)
		if err != nil || n <= since {
			continue
		}
		items = append(items, item{seq: n, name: e.Name()})
	}
	if len(items) == 0 {
		return nil, since
	}
	sort.Slice(items, func(i, j int) bool { return items[i].seq < items[j].seq })

	out := make([]map[string]interface{}, 0, len(items))
	var total int64
	next := since
	for i, it := range items {
		if i >= maxPullCount || total >= maxPullBytes {
			break
		}
		raw, err := os.ReadFile(filepath.Join(dir, it.name))
		if err != nil {
			continue
		}
		total += int64(len(raw))
		out = append(out, map[string]interface{}{"seq": it.seq, "data": string(raw)})
		next = it.seq
	}
	return out, next
}

// POST /v1/ack 已收到哪一条为止 → 之前的密文立即删除
func (s *Server) handleAck(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeErr(w, http.StatusMethodNotAllowed, "method", "只支持 POST")
		return
	}
	var in struct {
		RoomID   string `json:"roomId"`
		DeviceID string `json:"deviceId"`
		Token    string `json:"token"`
		UpTo     int64  `json:"upTo"`
	}
	if err := decodeBody(r, &in); err != nil {
		writeErr(w, http.StatusBadRequest, "bad_body", "请求格式不对")
		return
	}
	room := s.getRoom(in.RoomID)
	if room == nil {
		writeErr(w, http.StatusNotFound, "no_room", "配对已失效，请重新配对")
		return
	}
	me := sanitizeID(in.DeviceID)
	room.mu.Lock()
	defer room.mu.Unlock()
	if _, code := s.authDevice(room, me, in.Token); code != "" {
		writeErr(w, http.StatusForbidden, code, "没有权限")
		return
	}
	dir := s.inboxDir(room.ID, me)
	entries, err := os.ReadDir(dir)
	if err == nil {
		for _, e := range entries {
			if e.IsDir() || !strings.HasSuffix(e.Name(), ".env") {
				continue
			}
			n, err := strconv.ParseInt(strings.TrimSuffix(e.Name(), ".env"), 10, 64)
			if err == nil && n <= in.UpTo {
				os.Remove(filepath.Join(dir, e.Name()))
			}
		}
	}
	writeJSON(w, http.StatusOK, map[string]interface{}{"ok": true})
}

// ---------------------------------------------------------------- 过期清理

func (s *Server) cleanupLoop() {
	ticker := time.NewTicker(time.Minute)
	defer ticker.Stop()
	for {
		<-ticker.C
		s.cleanupOnce()
	}
}

func (s *Server) cleanupOnce() {
	s.limiter.sweep()
	now := time.Now()
	cutoff := now.Add(-s.cfg.TTL)

	s.mapMu.RLock()
	rooms := make([]*Room, 0, len(s.rooms))
	for _, r := range s.rooms {
		rooms = append(rooms, r)
	}
	s.mapMu.RUnlock()

	for _, room := range rooms {
		// 1) 过期的中转密文
		inboxRoot := filepath.Join(s.roomDir(room.ID), "inbox")
		if dirs, err := os.ReadDir(inboxRoot); err == nil {
			for _, d := range dirs {
				if !d.IsDir() {
					continue
				}
				files, err := os.ReadDir(filepath.Join(inboxRoot, d.Name()))
				if err != nil {
					continue
				}
				for _, f := range files {
					info, err := f.Info()
					if err != nil {
						continue
					}
					if info.ModTime().Before(cutoff) {
						os.Remove(filepath.Join(inboxRoot, d.Name(), f.Name()))
					}
				}
			}
		}

		// 2) 空房间（没有已批准设备）且最后活动早于保留期 → 整个删掉
		room.mu.Lock()
		alive := 0
		for _, d := range room.Devices {
			if d.Approved {
				alive++
			}
		}
		expired := room.CodeExpire.IsZero() == false && now.After(room.CodeExpire)
		if expired && room.Code != "" {
			room.Code = ""
			room.CodeExpire = time.Time{}
			s.persist(room)
		}
		idle := now.Sub(room.LastActive) > s.cfg.TTL
		room.mu.Unlock()

		if alive == 0 && idle {
			s.dropRoom(room.ID)
			log.Printf("房间 %s 已过期，已清理", room.ID)
		}
	}
}
