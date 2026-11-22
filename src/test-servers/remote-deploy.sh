#!/bin/sh
# On the test machine (as root): <slot> <source.tar.gz> <label> <services> <cserver hash> <rooms hash>
set -eu
SLOT=$1; TAR=$2; LABEL=$3; SERVICES=$4; CSERVER_HASH=$5; ROOMS_HASH=$6
case "$SLOT" in 0|1|2|3) ;; *) echo 'DEPLOY FAIL: slot must be 0-3'; exit 2 ;; esac
BASE=$((47100 + 10 * SLOT))
ROOT=/opt/oricoop/test/$SLOT
export DOTNET_ROOT=/opt/oricoop/dotnet HOME=/opt/oricoop/.build-home DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p "$ROOT" "$HOME" /etc/oricoop

exec 9>"$ROOT/.deploy.lock"
if ! flock -w 300 9; then echo "DEPLOY FAIL: slot $SLOT is being deployed by someone else"; exit 3; fi

rm -rf "$ROOT/src.new"; mkdir "$ROOT/src.new"
tar -xzf "$TAR" -C "$ROOT/src.new"
rm -f "$TAR"

install -m 644 "$ROOT"/src.new/src/test-servers/units/* /etc/systemd/system/
systemctl daemon-reload
printf 'ROOMS_PORT=%s\nCSERVER_PORT=%s\n' $((BASE + 1)) $((BASE + 2)) > "/etc/oricoop/test-$SLOT.env"

for svc in $SERVICES; do
  case $svc in
    cserver) proj=CServer ;; rooms) proj=RoomServer ;;
    *) echo "DEPLOY FAIL: unknown service $svc"; exit 2 ;;
  esac
  rm -rf "$ROOT/$svc.new"
  if ! "$DOTNET_ROOT/dotnet" publish "$ROOT/src.new/src/$proj/$proj.csproj" -c Release -o "$ROOT/$svc.new" --nologo -v q > "$ROOT/build-$svc.log" 2>&1; then
    tail -30 "$ROOT/build-$svc.log"; echo "DEPLOY FAIL: $proj build failed (slot $SLOT unchanged)"; exit 4
  fi
done

# All builds succeeded. Invalidate the old hash before swapping in each binary.
for svc in $SERVICES; do
  rm -f "$ROOT/$svc.hash"
  rm -rf "$ROOT/$svc.prev"
  if [ -d "$ROOT/$svc" ]; then mv "$ROOT/$svc" "$ROOT/$svc.prev"; fi
  mv "$ROOT/$svc.new" "$ROOT/$svc"
done
rm -rf "$ROOT/src"; mv "$ROOT/src.new" "$ROOT/src"
for svc in $SERVICES; do
  systemctl enable "oricoop-test-$svc@$SLOT" > /dev/null 2>&1
  systemctl restart "oricoop-test-$svc@$SLOT"
done
systemctl enable --now oricoop-test-prune.timer > /dev/null 2>&1

sleep 2
for svc in $SERVICES; do
  case $svc in
    cserver) proto=u; port=$((BASE + 2)) ;;
    rooms) proto=t; port=$((BASE + 1)) ;;
  esac
  if ! systemctl is-active --quiet "oricoop-test-$svc@$SLOT" || ! ss -H -ln"$proto" | grep -Eq ":$port[[:space:]]"; then
    echo "DEPLOY FAIL: oricoop-test-$svc@$SLOT is not active and listening on $port"; exit 5
  fi
  echo "oricoop-test-$svc@$SLOT: active on $port"
done
for svc in $SERVICES; do
  case $svc in cserver) hash=$CSERVER_HASH ;; rooms) hash=$ROOMS_HASH ;; esac
  printf '%s\n' "$hash" > "$ROOT/$svc.hash.new"
  mv -f "$ROOT/$svc.hash.new" "$ROOT/$svc.hash"
done
echo "$LABEL $(date -u +%Y-%m-%dT%H:%M:%SZ) services: $SERVICES" > "$ROOT/DEPLOYED"
echo "DEPLOY OK slot $SLOT: $LABEL"
