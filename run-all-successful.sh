#!/usr/bin/env bash
# Creates a complete successful payout flow: R11, RTC=true
# Usage: bash run-all-successful.sh

set -euo pipefail

API_BASE="https://api.secureexchange.co.za"
PAYOUT_API="https://stagingpayoutsapi.ozow.com/v1"
SITE_CODE="SEC-SEC-004"
PAYOUT_API_KEY="${OZOW_PAYOUT_API_KEY:-mMPd7XC1zJjDSAh5Wcpf9I6XBl}"
FNB_BANK_ID="3284a0ad-ba78-4838-8c2b-102981286a2b"
FNB_BRANCH="632005"
VALID_ACCOUNT="4050338500"
PGHOST="securex-db.chiwk8mqor05.af-south-1.rds.amazonaws.com"
PGUSER="securex"
PGDB="securex"
export PGPASSWORD='SecureX2025!'
TS=$(date +%s)

echo "========================================"
echo "  CREATING SUCCESSFUL PAYOUT — R11 RTC=true"
echo "  $(date)"
echo "========================================"

# Step 1: Auth
echo ""
echo "Step 1: Getting auth token..."
TOKEN=$(curl -s -X POST "$API_BASE/api/auth/token" \
  -H "Content-Type: application/json" \
  -d '{"email":"test@secureexchange.co.za"}' | jq -r '.token')
[ -z "$TOKEN" ] || [ "$TOKEN" = "null" ] && echo "❌ Auth failed" && exit 1
echo "✅ Token obtained"

# Step 2: Create transaction
echo ""
echo "Step 2: Creating transaction (R11.00)..."
TX_RESP=$(curl -s -X POST "$API_BASE/api/transactions" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
    \"buyerFullName\": \"Test Buyer\",
    \"buyerEmail\": \"buyer${TS}@test.co.za\",
    \"buyerPhone\": \"0821234567\",
    \"sellerFullName\": \"Test Seller\",
    \"sellerEmail\": \"seller${TS}@test.co.za\",
    \"sellerPhone\": \"0829876543\",
    \"itemTitle\": \"Ozow RTC Test\",
    \"itemDescription\": \"RTC=true integration test\",
    \"itemValue\": 11.00,
    \"sellerLocation\": \"Johannesburg\",
    \"serviceType\": 0,
    \"feePayer\": 0
  }")
TX_ID=$(echo "$TX_RESP" | jq -r '.Id // empty')
DEAL_REF=$(echo "$TX_RESP" | jq -r '.DealReference // empty')
SELLER_ID=$(echo "$TX_RESP" | jq -r '.Seller.Id // empty')
[ -z "$TX_ID" ] && echo "❌ Transaction failed: $TX_RESP" && exit 1
echo "✅ Transaction: $TX_ID ($DEAL_REF)"

# Step 3: Bank details
echo ""
echo "Step 3: Saving bank details..."
curl -s -X POST "$API_BASE/api/users/$SELLER_ID/bank-details" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
    \"accountNumber\": \"$VALID_ACCOUNT\",
    \"branchCode\": \"$FNB_BRANCH\",
    \"bankGroupId\": \"$FNB_BANK_ID\",
    \"idNumber\": \"8001015009087\"
  }" > /dev/null
echo "✅ Bank details saved"

# Step 4: Bypass payment
echo ""
echo "Step 4: Bypassing payment (DB update)..."
psql -h "$PGHOST" -p 5432 -U "$PGUSER" -d "$PGDB" --set=sslmode=require -q \
  -c "UPDATE transactions SET status = 'FundsSecured', version = 2, updated_at = NOW() WHERE \"Id\" = '$TX_ID';" 2>/dev/null
echo "✅ Status set to FundsSecured"

# Step 5: Start logistics
echo ""
echo "Step 5: Starting logistics..."
curl -s -X POST "$API_BASE/api/transactions/$TX_ID/start-logistics" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"actor":"seller","expectedVersion":2}' > /dev/null
echo "✅ Logistics started"

# Step 6: Mark delivered
echo ""
echo "Step 6: Marking delivered..."
DEL_RESP=$(curl -s -X POST "$API_BASE/api/transactions/$TX_ID/mark-delivered" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"actor":"seller","expectedVersion":3}')
VERSION=$(echo "$DEL_RESP" | jq -r '.Version // 4')
echo "✅ Delivered (version: $VERSION)"

# Step 7: Accept — triggers payout
echo ""
echo "Step 7: Accepting (triggers payout)..."
ACCEPT_RESP=$(curl -s -X POST "$API_BASE/api/transactions/$TX_ID/accept" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{\"actor\":\"buyer\",\"expectedVersion\":$VERSION}")
echo "$ACCEPT_RESP" | jq '{Status: .Status, Version: .Version}'
echo "✅ Accepted — waiting 15s for payout submission..."
sleep 15

# Step 8: Get payout ID
echo ""
echo "Step 8: Fetching payout ID..."
PAYOUT_ID=$(psql -h "$PGHOST" -p 5432 -U "$PGUSER" -d "$PGDB" --set=sslmode=require -t -q \
  -c "SELECT payout_id FROM pending_payouts WHERE deal_reference = '$DEAL_REF' LIMIT 1;" 2>/dev/null | xargs)

if [ -z "$PAYOUT_ID" ]; then
  echo "⚠️  Payout ID not found yet — check ECS logs"
  echo "   aws logs filter-log-events --log-group-name /ecs/securex-api --region af-south-1 --filter-pattern 'payout'"
else
  echo "✅ Payout ID: $PAYOUT_ID"

  # Step 9: Check status
  echo ""
  echo "Step 9: Checking payout status..."
  STATUS_RESP=$(curl -s -H "SiteCode: $SITE_CODE" -H "ApiKey: $PAYOUT_API_KEY" \
    "$PAYOUT_API/getpayout?payoutId=$PAYOUT_ID")
  echo "$STATUS_RESP" | jq '{isRtc: .isRtc, status: .payoutStatus.status, subStatus: .payoutStatus.subStatus}'
fi

echo ""
echo "========================================"
echo "  DONE"
echo "========================================"
echo "  Deal Ref:  $DEAL_REF"
echo "  Payout ID: ${PAYOUT_ID:-not available yet}"
echo "  Dashboard: https://stagingdash.ozow.com/MerchantAdmin/Payouts/Details/${PAYOUT_ID:-}"
echo "========================================"
