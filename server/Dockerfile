# 剪贴板助手 · 自建同步服务器（密文中转）
#
# 直接打包同目录下已经编译好的 clipsync-server（Linux x64 静态二进制），
# 所以构建时不需要联网下载 Go 工具链，镜像也很小。
#
# 构建：docker build -t clipsync-server:1.0.0 .
# 运行：docker run -d -p 47820:47820 -v $(pwd)/data:/data --name clipsync clipsync-server:1.0.0
#
# 说明：镜像里只跑一个二进制，不需要数据库；/data 里只有客户端给的密文，没有明文与密钥。

FROM alpine:3.20
RUN adduser -D -H -u 10001 clipsync \
    && mkdir -p /data \
    && chown -R clipsync:clipsync /data

COPY clipsync-server /usr/local/bin/clipsync-server
RUN chmod 0755 /usr/local/bin/clipsync-server

USER clipsync
ENV CLIP_PORT=47820 \
    CLIP_DATA=/data \
    CLIP_TTL_HOURS=168

EXPOSE 47820
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s \
    CMD wget -qO- "http://127.0.0.1:${CLIP_PORT}/v1/health" >/dev/null || exit 1

ENTRYPOINT ["/usr/local/bin/clipsync-server"]
