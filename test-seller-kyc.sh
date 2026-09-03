#!/usr/bin/env bash
# Seller KYC e2e smoke test.
#
# Covers:
#   1. Create transaction (buyer KYC/AML inline)
#   2. Save seller bank details + ID number
#   3. POST /api/transactions/{id}/start-seller-kyc → liveness token
#   4. Simulate SmileID seller liveness webhook callback
#   5. Verify seller IdCheckStatus + AmlStatus + LivenessStatus == Approved
#
# Usage:
#   API_BASE=https://securex-api-vjf3.onrender.com bash test-seller-kyc.sh

set -euo pipefail

BASE="${API_BASE:-https://securex-api-vjf3.onrender.com}"
TS="$(date +%s)"
BUYER_EMAIL="amina.clearwater@example.com"
SELLER_EMAIL="seller-kyc-seller-${TS}@test.com"
SELLER_ID_NUMBER="8001015009087"

echo "=== SecureX Seller KYC E2E test ==="
echo "API: $BASE"
echo ""

# ── 1. Auth ──────────────────────────────────────────────────────────────────
echo "[1/5] Obtaining JWT..."
TOKEN=$(curl -sS --fail-with-body --max-time 30 -X POST "$BASE/api/auth/token" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"$BUYER_EMAIL\"}" | jq -r '.token // empty')
[ -z "$TOKEN" ] && echo "FAIL: no token" >&2 && exit 1
echo "      PASS"

# ── 2. Create transaction ─────────────────────────────────────────────────────
echo "[2/5] Creating transaction..."
TX=$(curl -sS --fail-with-body --max-time 60 -X POST "$BASE/api/transactions" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "$(jq -n \
    --arg be "$BUYER_EMAIL" \
    --arg se "$SELLER_EMAIL" \
    '{
      BuyerFullName: "Amina Fatou Clearwater",
      BuyerEmail: $be,
      BuyerPhone: "0821234567",
      BuyerIdNumber: "0000000000000",  # documented SmileID sandbox identity
      SellerFullName: "Test Seller Person",
      SellerEmail: $se,
      SellerPhone: "0834567890",
      ItemTitle: "Seller KYC Test Item",
      ItemDescription: "Seller KYC e2e test",
      ItemValue: 500,
      SellerLocation: "Cape Town",
      ServiceType: "Standard",
      FeePayer: "Buyer"
    }')")
TX_ID=$(echo "$TX" | jq -r '.Id // .id // empty')
SELLER_ID=$(echo "$TX" | jq -r '.Seller.Id // .seller.id // empty')
DEAL_REF=$(echo "$TX" | jq -r '.DealReference // .dealReference // empty')
[ -z "$TX_ID" ] && echo "FAIL: no transaction ID — $TX" >&2 && exit 1
echo "      PASS — $DEAL_REF ($TX_ID) seller=$SELLER_ID"

# ── 3. Save seller bank details + ID number ───────────────────────────────────
echo "[3/5] Saving seller bank details + ID number..."
curl -sS --fail-with-body --max-time 30 -X POST "$BASE/api/users/$SELLER_ID/bank-details" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "$(jq -n \
    --arg id "$SELLER_ID_NUMBER" \
    '{
      accountNumber: "4050338500",
      branchCode: "632005",
      bankGroupId: "3284a0ad-ba78-4838-8c2b-102981286a2b",
      idNumber: $id
    }')" > /dev/null
echo "      PASS"

# ── 4. Start seller KYC — get liveness token ─────────────────────────────────
echo "[4/5] Starting seller KYC (liveness token)..."
KYC_RESP=$(curl -sS --fail-with-body --max-time 30 -X POST \
  "$BASE/api/transactions/$TX_ID/start-seller-kyc" \
  -H "Authorization: Bearer $TOKEN")
TOKEN_VAL=$(echo "$KYC_RESP" | jq -r '.token // empty')
PARTNER_ID=$(echo "$KYC_RESP" | jq -r '.partnerId // empty')
ENVIRONMENT=$(echo "$KYC_RESP" | jq -r '.environment // empty')
if [ -z "$TOKEN_VAL" ]; then
  echo "FAIL: no liveness token returned" >&2
  echo "$KYC_RESP" | jq . >&2
  exit 1
fi
echo "      PASS — env=$ENVIRONMENT partnerId=$PARTNER_ID token=${TOKEN_VAL:0:20}..."

# ── 5. Simulate SmileID seller liveness webhook ───────────────────────────────
echo "[5/5] Simulating SmileID seller liveness webhook..."
WEBHOOK_BODY=$(jq -n \
  --arg ref "$DEAL_REF" \
  --arg txid "$TX_ID" \
  '{
    status: "clear",
    job_id: "sim-liveness-job-001",
    partner_params: {
      deal_reference: $ref,
      internal_reference: $txid,
      verification_type: "seller_liveness"
    }
  }')

WEBHOOK_RESP=$(curl -sS --max-time 30 -o /dev/null -w "%{http_code}" \
  -X POST "$BASE/api/transactions/kyc-webhook" \
  -H "Content-Type: application/json" \
  -d "$WEBHOOK_BODY")

[ "$WEBHOOK_RESP" != "200" ] && echo "FAIL: webhook returned HTTP $WEBHOOK_RESP" >&2 && exit 1
echo "      PASS — webhook accepted (HTTP 200)"

# ── Verify seller statuses ────────────────────────────────────────────────────
echo ""
echo "Verifying seller KYC statuses..."
SELLER_DATA=$(curl -sS --fail-with-body --max-time 30 \
  "$BASE/api/users/$SELLER_ID" \
  -H "Authorization: Bearer $TOKEN")
KYC=$(echo "$SELLER_DATA" | jq -r '.idCheckStatus // .IdCheckStatus // "Unknown"')
AML=$(echo "$SELLER_DATA" | jq -r '.amlStatus // .AmlStatus // "Unknown"')
LIVENESS=$(echo "$SELLER_DATA" | jq -r '.livenessStatus // .LivenessStatus // "Unknown"')
echo "  IdCheckStatus : $KYC"
echo "  AmlStatus     : $AML"
echo "  LivenessStatus: $LIVENESS"

FAIL=0
[ "$KYC" != "Approved" ]     && echo "FAIL: IdCheckStatus=$KYC (expected Approved)" >&2     && FAIL=1
[ "$LIVENESS" != "Approved" ] && echo "FAIL: LivenessStatus=$LIVENESS (expected Approved)" >&2 && FAIL=1
[ "$AML" = "Failed" ]         && echo "FAIL: AmlStatus=Failed" >&2                            && FAIL=1
[ "$FAIL" = "1" ] && exit 1

echo ""
echo "PASS: Seller KYC e2e flow completed."
echo "Deal reference : $DEAL_REF"
echo "Seller ID      : $SELLER_ID"
