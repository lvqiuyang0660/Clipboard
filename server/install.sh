#!/usr/bin/env bash
# 剪贴板助手 · 自建同步服务器 一键安装脚本（Linux x64）
#
# 用法（上传本目录后，在服务器上执行）：
#   sudo bash install.sh                # 安装并启动（默认端口 47820）
#   sudo CLIP_PORT=50000 bash install.sh  # 换端口
#   sudo bash install.sh --uninstall    # 卸载（保留数据目录）
#
# 做的事：把 clipsync-server 装到 /usr/local/bin，建一个专用系统用户，
# 用 systemd 托管，数据放在 /var/lib/clipsync。全程不装数据库、不依赖其他软件。
set -euo pipefail

BIN_SRC="$(cd "$(dirname "$0")" && pwd)/clipsync-server"
BIN_DST="/usr/local/bin/clipsync-server"
DATA_DIR="${CLIP_DATA:-/var/lib/clipsync}"
PORT="${CLIP_PORT:-47820}"
TTL_HOURS="${CLIP_TTL_HOURS:-168}"
SERVICE="/etc/systemd/system/clipsync.service"
RUN_USER="clipsync"

info()  { printf '\033[32m[OK]\033[0m %s\n' "$1"; }
warn()  { printf '\033[33m[注意]\033[0m %s\n' "$1"; }
fail()  { printf '\033[31m[失败]\033[0m %s\n' "$1" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || fail "请用 root 运行：sudo bash install.sh"

if [ "${1:-}" = "--uninstall" ]; then
  systemctl stop clipsync 2>/dev/null || true
  systemctl disable clipsync 2>/dev/null || true
  rm -f "$SERVICE"
  systemctl daemon-reload
  rm -f "$BIN_DST"
  info "已卸载服务。数据仍保留在 $DATA_DIR（里面只有密文，可手动删除）"
  exit 0
fi

case "$(uname -m)" in
  x86_64|amd64) : ;;
  *) fail "这个程序只有 Linux 64 位（x86_64）版本，当前系统是 $(uname -m)" ;;
esac

[ -f "$BIN_SRC" ] || fail "当前目录里找不到 clipsync-server，请把程序和本脚本放在一起上传"
chmod +x "$BIN_SRC"

# 专用系统用户（不给登录 shell）
if ! id "$RUN_USER" >/dev/null 2>&1; then
  useradd --system --home "$DATA_DIR" --shell /usr/sbin/nologin "$RUN_USER" 2>/dev/null || true
fi

install -m 0755 "$BIN_SRC" "$BIN_DST"
mkdir -p "$DATA_DIR"
chown -R "$RUN_USER":"$RUN_USER" "$DATA_DIR"
chmod 700 "$DATA_DIR"

cat > "$SERVICE" <<EOF
[Unit]
Description=ClipboardHelper Sync Relay (ciphertext only)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$RUN_USER
Group=$RUN_USER
Environment=CLIP_PORT=$PORT
Environment=CLIP_DATA=$DATA_DIR
Environment=CLIP_TTL_HOURS=$TTL_HOURS
ExecStart=$BIN_DST
Restart=always
RestartSec=3
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ProtectHome=true
ReadWritePaths=$DATA_DIR

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable clipsync >/dev/null 2>&1 || true
systemctl restart clipsync
sleep 2

if systemctl is-active --quiet clipsync; then
  info "服务已启动，端口 $PORT，数据目录 $DATA_DIR"
else
  fail "服务没起来，执行 journalctl -u clipsync -n 50 看原因"
fi

echo
echo "----- 验证服务在跑（应该返回 ok:true）-----"
if command -v curl >/dev/null 2>&1; then
  curl -s "http://127.0.0.1:$PORT/v1/health" || warn "本机访问不通，检查端口是否被占用"
  echo
else
  warn "系统没有 curl，可以跳过这步，直接用客户端「测试连通」"
fi

echo
echo "----- 还差最后一步：放行端口 -----"
echo "1) 阿里云控制台 → 轻量应用服务器 → 你的实例 → 防火墙 → 添加规则"
echo "   应用类型：自定义   TCP   $PORT"
echo "2) 如果系统开了防火墙，再执行："
echo "   ufw allow $PORT/tcp        # Ubuntu/Debian"
echo "   firewall-cmd --add-port=$PORT/tcp --permanent && firewall-cmd --reload   # CentOS/Alibaba Cloud Linux"
echo
echo "之后在客户端：设置 → 同步 → 自建服务器，填「服务器公网 IP」和端口 $PORT，点「测试连通」。"
echo "查看日志：journalctl -u clipsync -f     # 只有连接与错误码，不含任何内容"
echo "停止服务：systemctl stop clipsync     卸载：sudo bash install.sh --uninstall"
