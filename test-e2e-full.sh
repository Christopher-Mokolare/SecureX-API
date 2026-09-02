#!/usr/bin/env bash
# SecureX full end-to-end test — buyer KYC/AML → payment → seller KYC → logistics → accept → payout.
#
# Covers:
#   1.  Health check
#   2.  JWT auth
#   3.  Create transaction (buyer Enhanced KYC + AML inline)
#   4.  Poll buyer KYC + AML approval
#   5.  Create Ozow collection payment link
#   6.  Simulate Ozow collection webhook → FundsSecured
#   7.  Save seller bank details + ID number
#   8.  Start seller KYC → verify sandbox id_number override (must be 0000000000000)
#   9.  Simulate SmileID seller liveness webhook → all 3 seller statuses Approved
#   10. Start logistics → LogisticsPending
#   11. Mark delivered → ItemDelivered
#   12. Buyer accepts → Completed + payout triggered
#   13. Verify final status = Completed
#
# Usage:
#   API_BASE=https://securex-api-vjf3.onrender.com bash test-e2e-full.sh
#
# Requires: curl, jq
# OZOW_ACCESS_TOKEN must be set (used to simulate the collection webhook).

set -euo pipefail

BASE="${API_BASE:-https://securex-api-vjf3.onrender.com}"
OZOW_TOKEN="${OZOW_ACCESS_TOKEN:-}"
POLL_SECONDS="${POLL_SECONDS:-3}"
TIMEOUT_SECONDS="${TIMEOUT_SECONDS:-90}"

BUYER_NAME="Amina Fatou Clearwater"
BUYER_EMAIL="amina.clearwater@example.com"
BUYER_ID="0000000000000"
TS="$(date +%s)"
SELLER_EMAIL="e2e-full-seller-${TS}@test.com"

command -v curl >/dev/null 2>&1 || { echo "FAIL: curl not found" >&2; exit 1; }
command -v jq   >/dev/null 2>&1 || { echo "FAIL: jq not found"   >&2; exit 1; }

if [ -z "$OZOW_TOKEN" ]; then
  echo "FAIL: OZOW_ACCESS_TOKEN env var is required to simulate the collection webhook." >&2
  echo "      Export it from your Render dashboard before running this script." >&2
  exit 1
fi

get()  { curl -sS --fail-with-body --max-time 60 -X GET  "$1" -H "Authorization: Bearer $TOKEN"; }
post() { curl -sS --fail-with-body --max-time 60 -X POST "$1" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "${2:-}"; }
anon() { curl -sS --fail-with-body --max-time 60 -X POST "$1" -H "Content-Type: application/json" -d "${2:-}"; }

echo "=== SecureX Full E2E Test ==="
echo "API   : $BASE"
echo "Buyer : $BUYER_NAME"
echo "Seller: $SELLER_EMAIL"
echo ""

# ── 1. Health ─────────────────────────────────────────────────────────────────
echo "[1/13] Health check..."
HEALTH="$(curl -sS --fail-with-body --max-time 30 "$BASE/health")"
[ "$(echo "$HEALTH" | jq -r '.ok // false')" = "true" ] || { echo "FAIL: $HEALTH" >&2; exit 1; }
echo "       PASS"

# ── 2. Auth ───────────────────────────────────────────────────────────────────
echo "[2/13] Obtaining JWT..."
TOKEN="$(anon "$BASE/api/auth/token" "{\"email\":\"$BUYER_EMAIL\"}" | jq -r '.token // empty')"
[ -z "$TOKEN" ] && echo "FAIL: no token" >&2 && exit 1
echo "       PASS"

# ── 3. Create transaction ─────────────────────────────────────────────────────
echo "[3/13] Creating transaction (buyer KYC + AML inline)..."
TX="$(post "$BASE/api/transactions" "$(jq -n \
  --arg bn "$BUYER_NAME" --arg be "$BUYER_EMAIL" --arg bi "$BUYER_ID" \
  --arg se "$SELLER_EMAIL" \
  '{
    BuyerFullName: $bn, BuyerEmail: $be, BuyerPhone: "0821234567", BuyerIdNumber: $bi,
    SellerFullName: "E2E Full Seller", SellerEmail: $se, SellerPhone: "0834567890",
    ItemTitle: "E2E Full Test Item", ItemDescription: "Full e2e escrow test",
    ItemValue: 10, SellerLocation: "Johannesburg", ServiceType: "Standard", FeePayer: "Buyer"
  }')")"
TX_ID="$(echo "$TX" | jq -r '.Id // .id // empty')"
DEAL_REF="$(echo "$TX" | jq -r '.DealReference // .dealReference // empty')"
SELLER_ID="$(echo "$TX" | jq -r '.Seller.Id // .seller.id // empty')"
VERSION="$(echo "$TX" | jq -r '.Version // .version // 0')"
[ -z "$TX_ID" ] && echo "FAIL: no txId — $(echo "$TX" | jq .)" >&2 && exit 1
echo "       PASS — $DEAL_REF ($TX_ID) seller=$SELLER_ID"

# ── 4. Poll buyer KYC + AML ───────────────────────────────────────────────────
echo "[4/13] Waiting for buyer KYC + AML (max ${TIMEOUT_SECONDS}s)..."
ELAPSED=0
while [ "$ELAPSED" -lt "$TIMEOUT_SECONDS" ]; do
  CURRENT="$(get "$BASE/api/transactions/$TX_ID")"
  KYC="$(echo "$CURRENT" | jq -r '.Buyer.IdCheckStatus // .buyer.idCheckStatus // "Unknown"')"
  AML="$(echo "$CURRENT" | jq -r '.Buyer.AmlStatus // .buyer.amlStatus // "Unknown"')"
  VERSION="$(echo "$CURRENT" | jq -r '.Version // .version // 0')"
  echo "       ${ELAPSED}s — KYC=$KYC AML=$AML"
  [ "$KYC" != "Pending" ] && [ "$AML" != "Pending" ] && break
  sleep "$POLL_SECONDS"
  ELAPSED=$((ELAPSED + POLL_SECONDS))
done
[ "$KYC" = "Approved" ] && [ "$AML" = "Approved" ] || { echo "FAIL: KYC=$KYC AML=$AML" >&2; exit 1; }
echo "       PASS — KYC=$KYC AML=$AML"

# ── 5. Payment link ───────────────────────────────────────────────────────────
echo "[5/13] Creating Ozow payment link..."
PAYMENT="$(post "$BASE/api/transactions/$TX_ID/payment-link" "{}")"
REDIRECT_URL="$(echo "$PAYMENT" | jq -r '.redirectUrl // .RedirectUrl // empty')"
[ -z "$REDIRECT_URL" ] && echo "FAIL: no redirectUrl — $(echo "$PAYMENT" | jq .)" >&2 && exit 1
echo "       PASS — $REDIRECT_URL"

# ── 6. Simulate Ozow collection webhook → FundsSecured ───────────────────────
echo "[6/13] Simulating Ozow collection webhook (FundsSecured)..."
SIM_STATUS="$(curl -sS --max-time 30 -o /dev/null -w "%{http_code}" \
  -X POST "$BASE/api/transactions/$TX_ID/simulate-payment" \
  -H "AccessToken: $OZOW_TOKEN")"
[ "$SIM_STATUS" = "200" ] || { echo "FAIL: simulate-payment returned HTTP $SIM_STATUS" >&2; exit 1; }
CURRENT="$(get "$BASE/api/transactions/$TX_ID")"
STATUS="$(echo "$CURRENT" | jq -r '.Status // .status // empty')"
VERSION="$(echo "$CURRENT" | jq -r '.Version // .version // 0')"
[ "$STATUS" = "FundsSecured" ] || { echo "FAIL: expected FundsSecured, got $STATUS" >&2; exit 1; }
echo "       PASS — status=$STATUS"

# ── 7. Save seller bank details + ID number ───────────────────────────────────
echo "[7/13] Saving seller bank details..."
post "$BASE/api/users/$SELLER_ID/bank-details" "$(jq -n '{
  accountNumber: "4050338500",
  branchCode: "632005",
  bankGroupId: "3284a0ad-ba78-4838-8c2b-102981286a2b",
  idNumber: "8001015009087"
}')" > /dev/null
echo "       PASS"

# ── 8. Start seller KYC — verify sandbox id_number override ──────────────────
echo "[8/13] Starting seller KYC (liveness token)..."
KYC_RESP="$(post "$BASE/api/transactions/$TX_ID/start-seller-kyc" "")"
TOKEN_VAL="$(echo "$KYC_RESP" | jq -r '.token // empty')"
ENVIRONMENT="$(echo "$KYC_RESP" | jq -r '.environment // empty')"
ID_NUMBER_IN_RESP="$(echo "$KYC_RESP" | jq -r '.idInfo.id_number // empty')"
[ -z "$TOKEN_VAL" ] && echo "FAIL: no liveness token — $(echo "$KYC_RESP" | jq .)" >&2 && exit 1
# In sandbox the backend must override the real ID with the test identity
if [ "$ENVIRONMENT" = "sandbox" ] && [ "$ID_NUMBER_IN_RESP" != "0000000000000" ]; then
  echo "FAIL: sandbox idInfo.id_number should be 0000000000000, got '$ID_NUMBER_IN_RESP'" >&2
  exit 1
fi
echo "       PASS — env=$ENVIRONMENT idInfo.id_number=$ID_NUMBER_IN_RESP token=${TOKEN_VAL:0:20}..."

# ── 9. Simulate seller liveness webhook → all 3 statuses Approved ─────────────
echo "[9/13] Simulating SmileID seller liveness webhook..."
WEBHOOK_STATUS="$(curl -sS --max-time 30 -o /dev/null -w "%{http_code}" \
  -X POST "$BASE/api/transactions/kyc-webhook" \
  -H "Content-Type: application/json" \
  -d "$(jq -n --arg ref "$DEAL_REF" --arg txid "$TX_ID" '{
    status: "clear",
    job_id: "sim-liveness-e2e-full",
    partner_params: {
      deal_reference: $ref,
      internal_reference: $txid,
      verification_type: "seller_liveness"
    }
  }')")"
[ "$WEBHOOK_STATUS" = "200" ] || { echo "FAIL: liveness webhook returned HTTP $WEBHOOK_STATUS" >&2; exit 1; }

SELLER_DATA="$(curl -sS --fail-with-body --max-time 30 "$BASE/api/users/$SELLER_ID" \
  -H "Authorization: Bearer $TOKEN")"
S_KYC="$(echo "$SELLER_DATA" | jq -r '.idCheckStatus // .IdCheckStatus // "Unknown"')"
S_AML="$(echo "$SELLER_DATA" | jq -r '.amlStatus // .AmlStatus // "Unknown"')"
S_LIV="$(echo "$SELLER_DATA" | jq -r '.livenessStatus // .LivenessStatus // "Unknown"')"
echo "       IdCheckStatus=$S_KYC AmlStatus=$S_AML LivenessStatus=$S_LIV"
[ "$S_KYC" = "Approved" ] || { echo "FAIL: IdCheckStatus=$S_KYC" >&2; exit 1; }
[ "$S_AML" = "Approved" ] || { echo "FAIL: AmlStatus=$S_AML (expected Approved)" >&2; exit 1; }
[ "$S_LIV" = "Approved" ] || { echo "FAIL: LivenessStatus=$S_LIV" >&2; exit 1; }
echo "       PASS"

# ── 10. Start logistics ───────────────────────────────────────────────────────
echo "[10/13] Starting logistics (FundsSecured → LogisticsPending)..."
CURRENT="$(get "$BASE/api/transactions/$TX_ID")"
VERSION="$(echo "$CURRENT" | jq -r '.Version // .version // 0')"
ADV="$(post "$BASE/api/transactions/$TX_ID/start-logistics" \
  "{\"actor\":\"seller\",\"expectedVersion\":$VERSION}")"
STATUS="$(echo "$ADV" | jq -r '.Status // .status // empty')"
VERSION="$(echo "$ADV" | jq -r '.Version // .version // 0')"
[ "$STATUS" = "LogisticsPending" ] || { echo "FAIL: expected LogisticsPending, got $STATUS" >&2; exit 1; }
echo "       PASS — status=$STATUS"

# ── 11. Mark delivered ────────────────────────────────────────────────────────
echo "[11/13] Marking item delivered (LogisticsPending → ItemDelivered)..."
ADV="$(post "$BASE/api/transactions/$TX_ID/mark-delivered" \
  "{\"actor\":\"seller\",\"expectedVersion\":$VERSION}")"
STATUS="$(echo "$ADV" | jq -r '.Status // .status // empty')"
VERSION="$(echo "$ADV" | jq -r '.Version // .version // 0')"
[ "$STATUS" = "ItemDelivered" ] || { echo "FAIL: expected ItemDelivered, got $STATUS" >&2; exit 1; }
echo "       PASS — status=$STATUS"

# ── 12. Buyer accepts → Completed ────────────────────────────────────────────
echo "[12/13] Buyer accepts item (ItemDelivered → Completed)..."
ADV="$(post "$BASE/api/transactions/$TX_ID/accept" \
  "{\"actor\":\"buyer\",\"expectedVersion\":$VERSION}")"
STATUS="$(echo "$ADV" | jq -r '.Status // .status // empty')"
[ "$STATUS" = "Completed" ] || { echo "FAIL: expected Completed, got $STATUS" >&2; exit 1; }
echo "       PASS — status=$STATUS"

# ── 13. Verify final state ────────────────────────────────────────────────────
echo "[13/13] Verifying final transaction state..."
FINAL="$(get "$BASE/api/transactions/$TX_ID")"
FINAL_STATUS="$(echo "$FINAL" | jq -r '.Status // .status // empty')"
[ "$FINAL_STATUS" = "Completed" ] || { echo "FAIL: final status=$FINAL_STATUS" >&2; exit 1; }
echo "       PASS — status=$FINAL_STATUS"

echo ""
echo "PASS: Full E2E completed."
echo "Deal reference : $DEAL_REF"
echo "Transaction ID : $TX_ID"
echo "Seller ID      : $SELLER_ID"
