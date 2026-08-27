#!/bin/bash
# E2E test: create a transaction and poll until KYC resolves.
# Usage: ./test-smileid-e2e.sh [base_url]
# Default base_url: http://localhost:8080
#
# SmileID sandbox only accepts specific test identities — name + ID must match.
# Known ZA test identity: Amina Fatou Clearwater / 9001015009087

BASE="${1:-http://localhost:8080}"
TS=$(date +%s)
BUYER_NAME="Amina Fatou Clearwater"
BUYER_ID="9001015009087"
BUYER_EMAIL="amina.clearwater@example.com"
SELLER_EMAIL="e2e-seller-${TS}@test.com"

echo "=== SecureX SmileID E2E Test ==="
echo "Base URL : $BASE"
echo "Buyer    : $BUYER_NAME / $BUYER_ID"
echo ""

# 1. Get JWT
echo "[1] Minting JWT..."
TOKEN=$(curl -sf -X POST "$BASE/api/auth/token" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"$BUYER_EMAIL\"}" | jq -r '.token')

if [ -z "$TOKEN" ] || [ "$TOKEN" = "null" ]; then
  echo "FAIL: could not mint JWT"
  exit 1
fi
echo "    OK"

# 2. Create transaction
echo "[2] Creating transaction..."
TX=$(curl -sf -X POST "$BASE/api/transactions" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $TOKEN" \
  -d "{
    \"BuyerFullName\": \"$BUYER_NAME\",
    \"BuyerEmail\": \"$BUYER_EMAIL\",
    \"BuyerPhone\": \"0821234567\",
    \"BuyerIdNumber\": \"$BUYER_ID\",
    \"SellerFullName\": \"Test Seller\",
    \"SellerEmail\": \"$SELLER_EMAIL\",
    \"SellerPhone\": \"0831234567\",
    \"ItemTitle\": \"E2E Test Item\",
    \"ItemDescription\": \"SmileID e2e test\",
    \"ItemValue\": 1000,
    \"SellerLocation\": \"Johannesburg\",
    \"ServiceType\": \"Standard\",
    \"FeePayer\": \"Buyer\"
  }")

TX_ID=$(echo "$TX" | jq -r '.Id')
DEAL_REF=$(echo "$TX" | jq -r '.DealReference')
INITIAL_KYC=$(echo "$TX" | jq -r '.Buyer.IdCheckStatus')

if [ -z "$TX_ID" ] || [ "$TX_ID" = "null" ]; then
  echo "FAIL: transaction creation failed"
  echo "$TX" | jq .
  exit 1
fi

echo "    TxID     : $TX_ID"
echo "    DealRef  : $DEAL_REF"
echo "    KYC (t=0): $INITIAL_KYC"

# 3. Poll until KYC resolves (max 60s)
echo "[3] Polling KYC status (max 60s)..."
for i in $(seq 1 20); do
  sleep 3
  POLL=$(curl -sf "$BASE/api/transactions/$TX_ID" \
    -H "Authorization: Bearer $TOKEN")
  KYC=$(echo "$POLL" | jq -r '.Buyer.IdCheckStatus')
  AML=$(echo "$POLL" | jq -r '.Buyer.AmlStatus')
  echo "    [${i}] IdCheckStatus=$KYC  AmlStatus=$AML"

  if [ "$KYC" != "Pending" ]; then
    echo ""
    if [ "$KYC" = "Approved" ] && [ "$AML" = "Approved" ]; then
      echo "PASS: KYC approved after ~$((i * 3))s"
    else
      echo "FAIL: KYC resolved to $KYC / AML=$AML"
    fi
    exit 0
  fi
done

echo ""
echo "FAIL: KYC still Pending after 60s — webhook may not have arrived"
exit 1
