#!/usr/bin/env bash
set -euo pipefail

OUTPUT_DIR="dist-simulator"
CONFIGURATION="Release"
SELF_CONTAINED=false
RUNTIME="linux-x64"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SIM_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --output) OUTPUT_DIR="$2"; shift 2 ;;
        --config) CONFIGURATION="$2"; shift 2 ;;
        --runtime) RUNTIME="$2"; shift 2 ;;
        --self-contained) SELF_CONTAINED=true; shift ;;
        -h|--help)
            echo "Usage: ./Simulation/deploy/build-simulator.sh [--output DIR] [--config Release] [--runtime RID] [--self-contained]"
            exit 0
            ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

if [[ "$OUTPUT_DIR" = /* ]]; then
    OUT_PATH="$OUTPUT_DIR"
else
    OUT_PATH="$SIM_ROOT/$OUTPUT_DIR"
fi

FRONTEND_PATH="$SIM_ROOT/acs.simulator.web"
BACKEND_PATH="$SIM_ROOT/ACS.Simulator.API/ACS.Simulator.API.csproj"
SERVER_OUT="$OUT_PATH/server"
WEB_DEST="$SERVER_OUT/wwwroot"

echo ""
echo "========================================================"
echo "|    ACS Simulator - Build and Package Script         |"
echo "========================================================"
echo ""
echo "  Simulation  : $SIM_ROOT"
echo "  Output      : $OUT_PATH"
echo "  Config      : $CONFIGURATION"
if [[ "$SELF_CONTAINED" == "true" ]]; then
    echo "  Mode        : Self-contained ($RUNTIME)"
else
    echo "  Mode        : Framework-dependent"
fi
echo ""

echo "[1/4] Building simulator web..."
cd "$FRONTEND_PATH"
if [[ ! -d node_modules ]]; then
    echo "      Installing npm dependencies (this may take a while)..."
    npm ci
fi
npm run build
echo "      Frontend build complete."

echo ""
echo "[2/4] Publishing simulator backend..."
rm -rf "$SERVER_OUT"

PUBLISH_ARGS=(
    publish "$BACKEND_PATH"
    -c "$CONFIGURATION"
    -o "$SERVER_OUT"
)

if [[ "$SELF_CONTAINED" == "true" ]]; then
    PUBLISH_ARGS+=(--self-contained true -r "$RUNTIME")
else
    PUBLISH_ARGS+=(--self-contained false)
fi

dotnet "${PUBLISH_ARGS[@]}"
if [[ -f "$SERVER_OUT/ACS.Simulator.API" ]]; then
    chmod +x "$SERVER_OUT/ACS.Simulator.API"
fi
echo "      Backend publish complete."

echo ""
echo "[3/4] Embedding simulator web into server/wwwroot..."
mkdir -p "$WEB_DEST"
cp -r "$FRONTEND_PATH/dist/." "$WEB_DEST/"
echo "      Frontend embedded."

echo ""
echo "[4/4] Copying simulator deployment scripts..."
for f in deploy-simulator.ps1 setup-simulator.ps1 setup-simulator.sh; do
    if [[ -f "$SCRIPT_DIR/$f" ]]; then
        cp "$SCRIPT_DIR/$f" "$OUT_PATH/"
        echo "      Copied $f"
    fi
done
chmod +x "$OUT_PATH/setup-simulator.sh" 2>/dev/null || true

echo ""
echo "========================================================"
echo "|               BUILD COMPLETE!                        |"
echo "========================================================"
echo ""
echo "  Package location : $OUT_PATH"
echo ""
echo "  Next steps:"
echo "    Windows : cd \"$OUT_PATH\" ; powershell -ExecutionPolicy Bypass -File deploy-simulator.ps1"
echo "    Linux   : cd \"$OUT_PATH\" ; chmod +x setup-simulator.sh ; sudo ./setup-simulator.sh"
echo ""
