#!/usr/bin/env bash
# Starts a persistent single-node replica set for MongoDelta development.
# Port 27117, replica set name: singleNodeReplSet
# Data lives in /tmp/mongodelta-dev/data (wiped on --reset)

set -e

MONGOD="/home/ashdev/.nuget/packages/ephemeralmongo8.runtime.linux-x64/2.0.0/runtimes/linux-x64/native/mongodb/bin/mongod"
MONGOSH="/home/ashdev/.local/bin/mongosh"
DATA_DIR="/tmp/mongodelta-dev/data"
LOG_FILE="/tmp/mongodelta-dev/mongod.log"
PID_FILE="/tmp/mongodelta-dev/mongod.pid"
PORT=27117
RS_NAME="singleNodeReplSet"
CONN="mongodb://127.0.0.1:${PORT}/?directConnection=true&replicaSet=${RS_NAME}"

if [[ "$1" == "--reset" ]]; then
  echo "Stopping any running instance and wiping data..."
  [[ -f "$PID_FILE" ]] && kill "$(cat "$PID_FILE")" 2>/dev/null || true
  rm -rf /tmp/mongodelta-dev
  echo "Done."
  exit 0
fi

if [[ "$1" == "--stop" ]]; then
  if [[ -f "$PID_FILE" ]]; then
    kill "$(cat "$PID_FILE")" && echo "Stopped (pid $(cat "$PID_FILE"))"
    rm -f "$PID_FILE"
  else
    echo "No PID file found."
  fi
  exit 0
fi

if [[ "$1" == "--status" ]]; then
  if [[ -f "$PID_FILE" ]] && kill -0 "$(cat "$PID_FILE")" 2>/dev/null; then
    echo "Running (pid $(cat "$PID_FILE"))"
    echo "Connection string: $CONN"
  else
    echo "Not running."
  fi
  exit 0
fi

# Already running?
if [[ -f "$PID_FILE" ]] && kill -0 "$(cat "$PID_FILE")" 2>/dev/null; then
  echo "Already running (pid $(cat "$PID_FILE"))"
  echo "Connection string: $CONN"
  exit 0
fi

mkdir -p "$DATA_DIR" "$(dirname "$LOG_FILE")"

echo "Starting mongod on port $PORT (replica set: $RS_NAME)..."
"$MONGOD" \
  --port "$PORT" \
  --dbpath "$DATA_DIR" \
  --replSet "$RS_NAME" \
  --bind_ip 127.0.0.1 \
  --logpath "$LOG_FILE" \
  --logappend \
  --fork \
  --quiet

echo "$("$MONGOD" --port "$PORT" --dbpath "$DATA_DIR" --version 2>&1 | head -1 || true)"

# Write pid (mongod --fork writes its own pid to the lock file; we read from log)
sleep 1
MONGOD_PID=$(cat "$DATA_DIR/mongod.lock" 2>/dev/null || true)
echo "${MONGOD_PID}" > "$PID_FILE"

# Initiate replica set if not already done
echo "Checking replica set status..."
RS_STATUS=$("$MONGOSH" --port "$PORT" --quiet --eval "
try {
  const s = rs.status();
  print(s.ok === 1 ? 'OK' : 'NEEDS_INIT');
} catch(e) {
  print('NEEDS_INIT');
}
" 2>/dev/null || echo "NEEDS_INIT")

if [[ "$RS_STATUS" == *"NEEDS_INIT"* ]]; then
  echo "Initiating replica set..."
  "$MONGOSH" --port "$PORT" --quiet --eval "
    rs.initiate({
      _id: '${RS_NAME}',
      members: [{ _id: 0, host: '127.0.0.1:${PORT}' }]
    });
  "
  # Wait for primary election
  for i in {1..15}; do
    ROLE=$("$MONGOSH" --port "$PORT" --quiet --eval "db.hello().isWritablePrimary" 2>/dev/null || echo "false")
    [[ "$ROLE" == "true" ]] && break
    sleep 1
  done
  echo "Primary elected."
fi

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  MongoDelta dev server ready"
echo "  Port     : $PORT"
echo "  Replica  : $RS_NAME"
echo "  Data     : $DATA_DIR"
echo "  Log      : $LOG_FILE"
echo "  PID      : $MONGOD_PID"
echo ""
echo "  Connection string:"
echo "  $CONN"
echo ""
echo "  Connect:  mongosh \"$CONN\""
echo "  Stop:     ./dev-server.sh --stop"
echo "  Reset:    ./dev-server.sh --reset"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
