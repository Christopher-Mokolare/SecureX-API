#!/usr/bin/env bash
# SecureX end-to-end smoke test.
#
# Covers:
#   1. JWT authentication
#   2. Transaction creation
#   3. SmileID Enhanced KYC submission and webhook result
#   4. SmileID AML screening result
#   5. Transaction retrieval and status correlation
#   6. Ozow collection payment-link creation
#
# Usage:
#   API_BASE=https://securex-api-vjf3.onrender.com ./test-smileid-e2e.sh
#
# The default identity is the documented SmileID sandbox identity. Do not
# replace its name, email, or ID unless using another documented test record.

set -euo pipefail

BASE="${API_BASE:-https://securex-api-vjf3.onrender.com}"
POLL_SECONDS="${POLL_SECONDS:-3}"
TIMEOUT_SECONDS="${TIMEOUT_SECONDS:-90}"
BUYER_NAME="Amina Fatou Clearwater"
BUYER_EMAIL="amina.clearwater@example.com"
BUYER_ID="0000000000000"
TIMESTAMP="$(date +%s)"
SELLER_EMAIL="e2e-seller-${TIMESTAMP}@test.com"

require_command() {
  command -v "$1" >/dev/null 2>&1 || {
    echo "FAIL: required command not found: $1" >&2
    exit 1
  }
}

require_command curl
require_command jq

request() {
  local method="$1"
  local url="$2"
  local token="${3:-}"
  local body="${4:-}"
  local args=(-sS --fail-with-body --max-time 60 -X "$method" "$url")

  if [ -n "$token" ]; then
    args+=(-H "Authorization: Bearer $token")
  fi
  if [ -n "$body" ]; then
    args+=(-H "Content-Type: application/json" -d "$body")
  fi

  curl "${args[@]}"
}

echo "=== SecureX full E2E test ==="
echo "API: $BASE"
echo "Buyer: $BUYER_NAME"
echo ""

echo "[1/6] Checking API health..."
HEALTH="$(curl -sS --fail-with-body --max-time 30 "$BASE/health")"
if [ "$(echo "$HEALTH" | jq -r '.ok // false')" != "true" ]; then
  echo "FAIL: API health check returned an unexpected response: $HEALTH" >&2
  exit 1
fi
echo "      PASS"

echo "[2/6] Obtaining JWT..."
TOKEN_RESPONSE="$(request POST "$BASE/api/auth/token" "" "{\"email\":\"$BUYER_EMAIL\"}")"
TOKEN="$(echo "$TOKEN_RESPONSE" | jq -r '.token // empty')"
if [ -z "$TOKEN" ]; then
  echo "FAIL: auth response did not contain a token" >&2
  exit 1
fi
echo "      PASS"

echo "[3/6] Creating transaction and starting KYC/AML..."
CREATE_BODY="$(jq -n \
  --arg buyerName "$BUYER_NAME" \
  --arg buyerEmail "$BUYER_EMAIL" \
  --arg buyerId "$BUYER_ID" \
  --arg sellerEmail "$SELLER_EMAIL" \
  '{
    BuyerFullName: $buyerName,
    BuyerEmail: $buyerEmail,
    BuyerPhone: "0821234567",
    BuyerIdNumber: $buyerId,
    SellerFullName: "SecureX E2E Seller",
    SellerEmail: $sellerEmail,
    SellerPhone: "0834567890",
    ItemTitle: "SecureX E2E Test Item",
    ItemDescription: "Automated end-to-end escrow integration test item.",
    ItemValue: 1000,
    SellerLocation: "Johannesburg",
    ServiceType: "Standard",
    FeePayer: "Buyer"
  }')"
TX="$(request POST "$BASE/api/transactions" "$TOKEN" "$CREATE_BODY")"
TX_ID="$(echo "$TX" | jq -r '.Id // .id // empty')"
DEAL_REF="$(echo "$TX" | jq -r '.DealReference // .dealReference // empty')"
if [ -z "$TX_ID" ] || [ -z "$DEAL_REF" ]; then
  echo "FAIL: transaction response did not contain an ID and deal reference" >&2
  echo "$TX" | jq . >&2
  exit 1
fi
echo "      PASS — $DEAL_REF ($TX_ID)"

echo "[4/6] Waiting for KYC and AML decisions (max ${TIMEOUT_SECONDS}s)..."
ELAPSED=0
while [ "$ELAPSED" -lt "$TIMEOUT_SECONDS" ]; do
  CURRENT="$(request GET "$BASE/api/transactions/$TX_ID" "$TOKEN")"
  KYC="$(echo "$CURRENT" | jq -r '.Buyer.IdCheckStatus // .buyer.idCheckStatus // "Unknown"')"
  AML="$(echo "$CURRENT" | jq -r '.Buyer.AmlStatus // .buyer.amlStatus // "Unknown"')"
  echo "      ${ELAPSED}s — KYC=$KYC AML=$AML"

  if [ "$KYC" != "Pending" ] && [ "$AML" != "Pending" ]; then
    break
  fi

  sleep "$POLL_SECONDS"
  ELAPSED=$((ELAPSED + POLL_SECONDS))
done

if [ "$KYC" != "Approved" ] || [ "$AML" != "Approved" ]; then
  echo "FAIL: verification did not pass — KYC=$KYC AML=$AML" >&2
  exit 1
fi
echo "      PASS — KYC and AML approved"

echo "[5/6] Verifying transaction lookup by deal reference..."
BY_REF="$(request GET "$BASE/api/transactions/ref/$DEAL_REF" "$TOKEN")"
LOOKUP_ID="$(echo "$BY_REF" | jq -r '.Id // .id // empty')"
if [ "$LOOKUP_ID" != "$TX_ID" ]; then
  echo "FAIL: deal-reference lookup returned the wrong transaction" >&2
  exit 1
fi
echo "      PASS"

echo "[6/6] Creating Ozow collection payment link..."
PAYMENT="$(request POST "$BASE/api/transactions/$TX_ID/payment-link" "$TOKEN" "{}")"
REDIRECT_URL="$(echo "$PAYMENT" | jq -r '.redirectUrl // .RedirectUrl // empty')"
if [ -z "$REDIRECT_URL" ]; then
  echo "FAIL: payment-link response did not contain redirectUrl" >&2
  echo "$PAYMENT" | jq . >&2
  exit 1
fi
echo "      PASS — payment link created"
echo ""
echo "PASS: SecureX KYC, AML, transaction, and payment-link E2E flow completed."
echo "Deal reference: $DEAL_REF"
echo "Payment URL: $REDIRECT_URL"
