#!/usr/bin/env bash
set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
CYAN='\033[0;36m'; WHITE='\033[1;37m'; GRAY='\033[0;37m'; NC='\033[0m'

read_input() {
    local prompt="$1"
    local default="${2:-}"
    local result
    if [[ -n "$default" ]]; then
        printf "${WHITE}  %s [%s] : ${NC}" "$prompt" "$default" >/dev/tty
    else
        printf "${WHITE}  %s : ${NC}" "$prompt" >/dev/tty
    fi
    read -r result </dev/tty
    echo "${result:-$default}"
}

get_primary_ip() {
    local ip
    ip=$(ip route get 1.1.1.1 2>/dev/null | awk '{for (i=1;i<=NF;i++) if ($i=="src") {print $(i+1); exit}}')
    if [[ -z "$ip" ]]; then
        ip=$(hostname -I 2>/dev/null | awk '{print $1}')
    fi
    echo "${ip:-127.0.0.1}"
}

SETUP_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVER_DIR="$SETUP_DIR/server"
APP_SETTINGS="$SERVER_DIR/appsettings.Production.json"
EXECUTABLE="$SERVER_DIR/ACS.Simulator.API"
SERVICE_FILE="/etc/systemd/system/acs-simulator.service"
SERVICE_NAME="acs-simulator"

if [[ ! -d "$SERVER_DIR" ]]; then
    echo -e "${RED}  [FAIL] Cannot find server folder at: $SERVER_DIR${NC}"
    exit 1
fi

DEF_PORT="9060"
DEF_MQTT_HOST="127.0.0.1"
DEF_MQTT_PORT="1883"
DEF_ACS_API="http://localhost:9050"

if [[ -f "$APP_SETTINGS" ]] && command -v python3 >/dev/null 2>&1; then
    DEF_PORT=$(python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('Urls','http://*:9060').split(':')[-1])" < "$APP_SETTINGS" 2>/dev/null || echo "$DEF_PORT")
    DEF_MQTT_HOST=$(python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('Simulator',{}).get('MqttHost','127.0.0.1'))" < "$APP_SETTINGS" 2>/dev/null || echo "$DEF_MQTT_HOST")
    DEF_MQTT_PORT=$(python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('Simulator',{}).get('MqttPort',1883))" < "$APP_SETTINGS" 2>/dev/null || echo "$DEF_MQTT_PORT")
    DEF_ACS_API=$(python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('Simulator',{}).get('AcsApiBaseUrl','http://localhost:9050'))" < "$APP_SETTINGS" 2>/dev/null || echo "$DEF_ACS_API")
fi

echo ""
echo -e "${CYAN}================================================================${NC}"
echo -e "${CYAN}|         ACS Simulator - Setup and Configuration              |${NC}"
echo -e "${CYAN}================================================================${NC}"
echo ""

SIM_PORT=$(read_input "Simulator Port" "$DEF_PORT")
MQTT_HOST=$(read_input "MQTT Broker Address" "$DEF_MQTT_HOST")
MQTT_PORT=$(read_input "MQTT Broker Port" "$DEF_MQTT_PORT")
ACS_API_BASE_URL=$(read_input "ACS API Base URL" "$DEF_ACS_API")

SIM_PORT="$SIM_PORT" \
MQTT_HOST="$MQTT_HOST" \
MQTT_PORT="$MQTT_PORT" \
ACS_API_BASE_URL="$ACS_API_BASE_URL" \
APP_SETTINGS="$APP_SETTINGS" \
python3 - <<'PYEOF'
import json, os

data = {
    "Urls": f"http://*:{os.environ['SIM_PORT']}",
    "Simulator": {
        "MqttHost": os.environ["MQTT_HOST"],
        "MqttPort": int(os.environ["MQTT_PORT"]),
        "AcsApiBaseUrl": os.environ["ACS_API_BASE_URL"].rstrip("/")
    }
}

with open(os.environ["APP_SETTINGS"], "w", encoding="utf-8") as f:
    json.dump(data, f, ensure_ascii=False, indent=2)
PYEOF

echo -e "${GREEN}  [OK] Written: $APP_SETTINGS${NC}"

if [[ -f "$EXECUTABLE" ]]; then
    chmod +x "$EXECUTABLE"
fi

IS_ROOT=false
if [[ "$(id -u)" -eq 0 ]]; then
    IS_ROOT=true
fi

if $IS_ROOT; then
    SERVICE_USER=$(read_input "Run service as user" "$(logname 2>/dev/null || whoami)")
    cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=ACS Simulator
After=network.target

[Service]
Type=simple
User=${SERVICE_USER}
WorkingDirectory=${SERVER_DIR}
ExecStart=${EXECUTABLE}
Restart=always
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
StandardOutput=journal
StandardError=journal
SyslogIdentifier=acs-simulator

[Install]
WantedBy=multi-user.target
EOF
    systemctl daemon-reload
    systemctl enable "$SERVICE_NAME"
    printf "${WHITE}  Start service now? [Y/n] : ${NC}" >/dev/tty
    read -r start_now </dev/tty
    start_now="${start_now:-y}"
    if [[ "$start_now" =~ ^[Yy]$ ]]; then
        systemctl restart "$SERVICE_NAME"
    fi
else
    echo -e "${YELLOW}  [WARN] Not running as root. Skipping systemd service installation.${NC}"
fi

HOST_IP="$(get_primary_ip)"
echo ""
echo -e "  ${CYAN}Simulator URL :${NC} http://${HOST_IP}:${SIM_PORT}"
echo -e "  ${CYAN}Swagger UI    :${NC} http://${HOST_IP}:${SIM_PORT}/swagger"
echo -e "  ${GRAY}Config file   : $APP_SETTINGS${NC}"
echo ""
