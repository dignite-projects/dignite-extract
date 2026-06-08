#!/bin/sh
# 由 nginx 官方镜像 entrypoint 在启动前自动执行（/docker-entrypoint.d/*.sh，按字典序）。
#
# 作用：从构建产物 index.html 的 <base href="..."> 自动推导 SPA 的部署路径，并据此生成
# nginx 配置。部署路径是发布者在构建时用 `nx build host --base-href=/foo/` 决定的（默认 "/"），
# 这里只是让容器跟随它——单一知识源、零漂移，**不在仓库里写死任何子路径**。
#
#   根部署：     nx build host                       → <base href="/">      → 容器在 / 伺服
#   子路径部署： nx build host --base-href=/admin/   → <base href="/admin/"> → 容器在 /admin/ 伺服
#
# 子路径部署下，反向代理把该前缀（如 /admin/*）原样转发给本容器即可，**无需** strip 前缀。
set -eu

HTML_DIR=/usr/share/nginx/html
CONF=/etc/nginx/conf.d/default.conf

# 从 index.html 提取 <base href>，缺省回退根路径
BASE=$(sed -n 's/.*<base href="\([^"]*\)".*/\1/p' "$HTML_DIR/index.html" 2>/dev/null | head -n1)
[ -z "$BASE" ] && BASE="/"
# 规范化：保证前后都有斜杠（"/admin" → "/admin/"，"admin/" → "/admin/"）
case "$BASE" in /*) ;; *) BASE="/$BASE" ;; esac
case "$BASE" in */) ;; *) BASE="$BASE/" ;; esac

{
    echo "server {"
    echo "    listen       80;"
    echo "    listen  [::]:80;"
    echo "    server_name  _;"
    echo ""
    echo "    # 由 40-spa-base-path.sh 依据 index.html 的 <base href=\"$BASE\"> 生成。"
    echo ""
    echo "    # ABP remoteEnv：<base>getEnvConfig → dynamic-env.json（部署期配置，覆盖静态默认值）"
    echo "    location = ${BASE}getEnvConfig {"
    echo "        default_type 'application/json';"
    echo "        add_header 'Access-Control-Allow-Origin' '*' always;"
    echo "        add_header 'Access-Control-Allow-Methods' 'GET, POST, OPTIONS' always;"
    echo "        alias ${HTML_DIR}/dynamic-env.json;"
    echo "    }"
    echo ""

    if [ "$BASE" != "/" ]; then
        NOSLASH=${BASE%/}
        echo "    # 裸前缀 / 根都补成带尾斜杠形式，否则 <base href> 下相对资源会解析错位"
        echo "    # （301 会保留 query string，OAuth 回调 ?code= 不受影响）"
        echo "    location = ${NOSLASH} { return 301 ${BASE}; }"
        echo "    location = / { return 301 ${BASE}; }"
        echo ""
    fi

    echo "    # 静态资源 + Angular client-side 路由 fallback"
    echo "    location ${BASE} {"
    echo "        alias  ${HTML_DIR}/;"
    echo "        index  index.html index.htm;"
    echo "        try_files \$uri \$uri/ ${BASE}index.html;"
    echo "    }"
    echo ""
    echo "    error_page   500 502 503 504  /50x.html;"
    echo "    location = /50x.html {"
    echo "        root   ${HTML_DIR};"
    echo "    }"
    echo "}"
} > "$CONF"

echo "[40-spa-base-path] nginx configured to serve SPA at base path: $BASE"
