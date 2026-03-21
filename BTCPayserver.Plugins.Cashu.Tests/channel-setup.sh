#!/bin/sh
# Sets up the Lightning network topology for integration/E2E tests.
#
# Topology:
#   customer_lnd --> mint_lnd  (customer pays mint invoices to get tokens)
#   mint_lnd --> merchant_lnd  (mint melts tokens by paying merchant invoices)
#
# customer_lnd, merchant_lnd: no-macaroons=1, plain HTTP
# mint_lnd: macaroons enabled, plain HTTP REST with Grpc-Metadata-Macaroon header

set -eu

CUSTOMER_LND="${CUSTOMER_LND_URL:-http://customer_lnd:8080}"
MERCHANT_LND="${MERCHANT_LND_URL:-http://merchant_lnd:8080}"
MINT_LND="${MINT_LND_URL:-http://mint_lnd:8080}"
MINT_MACAROON="${MINT_MACAROON_PATH:-/mint_lnd_data/data/chain/bitcoin/regtest/admin.macaroon}"

BTC_RPC_URL="${BTC_RPC_URL:-http://bitcoind:43782}"
BTC_RPC_USER="${BTC_RPC_USER:-ceiwHEbqWI83}"
BTC_RPC_PASS="${BTC_RPC_PASS:-DwubwWsoo3}"

log() { echo "[channel-setup] $*"; }

# ── Helpers ───────────────────────────────────────────────────────────────────

btc_rpc() {
    METHOD="$1"; PARAMS="$2"
    RESP=$(curl -sf \
        -u "${BTC_RPC_USER}:${BTC_RPC_PASS}" \
        -H "Content-Type: application/json" \
        --data "{\"jsonrpc\":\"1.0\",\"id\":\"setup\",\"method\":\"${METHOD}\",\"params\":[${PARAMS}]}" \
        "${BTC_RPC_URL}")
    echo "$RESP" | jq -r '.result'
}

lnd_get() {
    URL="$1"; MACAROON="${2:-}"
    if [ -n "$MACAROON" ]; then
        curl -sf -H "Grpc-Metadata-Macaroon: ${MACAROON}" "${URL}"
    else
        curl -sf "${URL}"
    fi
}

lnd_post() {
    URL="$1"; BODY="$2"; MACAROON="${3:-}"
    if [ -n "$MACAROON" ]; then
        curl -s -X POST -H "Content-Type: application/json" \
            -H "Grpc-Metadata-Macaroon: ${MACAROON}" \
            --data "${BODY}" "${URL}"
    else
        curl -s -X POST -H "Content-Type: application/json" \
            --data "${BODY}" "${URL}"
    fi
}

macaroon_hex() {
    od -An -tx1 "${MINT_MACAROON}" | tr -d ' \n'
}

wait_lnd_synced() {
    NAME="$1"; URL="$2"; MACAROON="${3:-}"
    log "Waiting for ${NAME} to sync..."
    i=0
    while [ "$i" -lt 120 ]; do
        RESP=$(lnd_get "${URL}/v1/getinfo" "${MACAROON}" 2>/dev/null || true)
        if [ -n "$RESP" ] && echo "$RESP" | jq -e '.synced_to_chain == true' >/dev/null 2>&1; then
            log "${NAME} synced."
            return 0
        fi
        sleep 2; i=$((i+1))
    done
    log "ERROR: ${NAME} did not sync in time." >&2; exit 1
}

lnd_new_address() {
    URL="$1"; MACAROON="${2:-}"
    i=0
    while [ "$i" -lt 15 ]; do
        RESP=$(lnd_get "${URL}/v1/newaddress" "${MACAROON}" 2>/dev/null || true)
        if [ -n "$RESP" ]; then
            ADDR=$(echo "$RESP" | jq -r '.address // empty' 2>/dev/null || true)
            if [ -n "$ADDR" ]; then echo "$ADDR"; return 0; fi
            log "newaddress attempt $i: $RESP"
        fi
        sleep 3; i=$((i+1))
    done
    log "ERROR: failed to get address from ${URL}" >&2; exit 1
}

lnd_pubkey() {
    URL="$1"; MACAROON="${2:-}"
    lnd_get "${URL}/v1/getinfo" "${MACAROON}" | jq -r '.identity_pubkey'
}

lnd_connect() {
    URL="$1"; PUBKEY="$2"; HOST="$3"; MACAROON="${4:-}"
    log "Connecting to ${HOST} (${PUBKEY%"${PUBKEY#????????}"}...)"
    lnd_post "${URL}/v1/peers" \
        "{\"addr\":{\"pubkey\":\"${PUBKEY}\",\"host\":\"${HOST}\"},\"perm\":true}" \
        "${MACAROON}" >/dev/null 2>&1 || true  # ignore "already connected"
}

lnd_open_channel() {
    URL="$1"; PUBKEY="$2"; LOCAL_SAT="$3"; PUSH_SAT="$4"; MACAROON="${5:-}"
    log "Opening channel to ${PUBKEY%"${PUBKEY#????????}"}... (${LOCAL_SAT} sat, push ${PUSH_SAT} sat)"
    RESP=$(lnd_post "${URL}/v1/channels" \
        "{\"node_pubkey_string\":\"${PUBKEY}\",\"local_funding_amount\":${LOCAL_SAT},\"push_sat\":${PUSH_SAT}}" \
        "${MACAROON}")
    log "OpenChannel response: ${RESP}"
    if echo "$RESP" | jq -e '.code // empty' >/dev/null 2>&1; then
        log "ERROR: OpenChannel failed" >&2; exit 1
    fi
}

wait_channel_active() {
    NAME="$1"; URL="$2"; REMOTE_PUBKEY="$3"; MACAROON="${4:-}"
    log "Waiting for channel ${NAME} to become active..."
    i=0
    while [ "$i" -lt 120 ]; do
        RESP=$(lnd_get "${URL}/v1/channels" "${MACAROON}" 2>/dev/null || true)
        if [ -n "$RESP" ]; then
            ACTIVE=$(echo "$RESP" | jq -r \
                --arg pk "$REMOTE_PUBKEY" \
                '[.channels[] | select(.remote_pubkey == $pk and .active == true)] | length > 0' \
                2>/dev/null || echo "false")
            if [ "$ACTIVE" = "true" ]; then log "Channel ${NAME} active."; return 0; fi
            COUNT=$(echo "$RESP" | jq '.channels | length' 2>/dev/null || echo "?")
            log "  attempt $i: ${COUNT} channel(s), waiting for active channel to ${REMOTE_PUBKEY%"${REMOTE_PUBKEY#????????}"}..."
        else
            log "  attempt $i: no response from ${URL}/v1/channels"
        fi
        sleep 2; i=$((i+1))
    done
    log "ERROR: channel ${NAME} not active in time." >&2; exit 1
}

# ── Main ──────────────────────────────────────────────────────────────────────

log "Starting Lightning topology setup..."

MINT_HEX=$(macaroon_hex)

wait_lnd_synced "customer_lnd" "${CUSTOMER_LND}"
wait_lnd_synced "merchant_lnd" "${MERCHANT_LND}"
wait_lnd_synced "mint_lnd"     "${MINT_LND}" "${MINT_HEX}"

CUSTOMER_ADDR=$(lnd_new_address "${CUSTOMER_LND}")
MINT_ADDR=$(lnd_new_address "${MINT_LND}" "${MINT_HEX}")
log "customer_lnd: ${CUSTOMER_ADDR}"
log "mint_lnd:     ${MINT_ADDR}"

# Mine 101 blocks to each LND wallet so coinbase rewards mature (COINBASE_MATURITY=100).
# bitcoind's own wallet is not funded, so sendtoaddress cannot be used.
log "Mining 101 blocks to customer (coinbase maturity)..."
btc_rpc "generatetoaddress" "101, \"${CUSTOMER_ADDR}\"" >/dev/null

log "Mining 101 blocks to mint (coinbase maturity)..."
btc_rpc "generatetoaddress" "101, \"${MINT_ADDR}\"" >/dev/null

log "Mining 6 confirmation blocks..."
btc_rpc "generatetoaddress" "6, \"${CUSTOMER_ADDR}\"" >/dev/null

wait_lnd_synced "customer_lnd" "${CUSTOMER_LND}"
wait_lnd_synced "mint_lnd"     "${MINT_LND}" "${MINT_HEX}"

MINT_PUBKEY=$(lnd_pubkey "${MINT_LND}" "${MINT_HEX}")
MERCHANT_PUBKEY=$(lnd_pubkey "${MERCHANT_LND}")
log "mint_lnd pubkey:     ${MINT_PUBKEY}"
log "merchant_lnd pubkey: ${MERCHANT_PUBKEY}"

lnd_connect "${CUSTOMER_LND}" "${MINT_PUBKEY}" "mint_lnd:9735"
lnd_open_channel "${CUSTOMER_LND}" "${MINT_PUBKEY}" 5000000 2000000

lnd_connect "${MINT_LND}" "${MERCHANT_PUBKEY}" "merchant_lnd:9735" "${MINT_HEX}"
lnd_open_channel "${MINT_LND}" "${MERCHANT_PUBKEY}" 5000000 2000000 "${MINT_HEX}"

log "Mining 6 blocks to confirm channels..."
btc_rpc "generatetoaddress" "6, \"${CUSTOMER_ADDR}\"" >/dev/null

wait_channel_active "customer->mint"  "${CUSTOMER_LND}" "${MINT_PUBKEY}"
wait_channel_active "mint->merchant"  "${MINT_LND}"     "${MERCHANT_PUBKEY}" "${MINT_HEX}"

log "Lightning topology ready!"
