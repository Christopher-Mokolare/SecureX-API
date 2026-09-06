#!/usr/bin/env bash
set -euo pipefail

SITE_CODE="SEC-SEC-004"
API_KEY="${OZOW_PAYOUT_API_KEY:?Set OZOW_PAYOUT_API_KEY}"
BASE_URL="https://stagingpayoutsapi.ozow.com/v1"
NOTIFY_URL="https://securex-api-vjf3.onrender.com/securex/payout-notification"
VERIFY_URL="https://furnacelike-adrienne-fourpenny.ngrok-free.dev/securex/payout-verify"
BANK_GROUP_ID="4816019c-3314-4c80-8b6b-b2cd16dcc4ec"
BRANCH_CODE="250655"
ACCOUNT_NUMBER="62285724655"
TS="$(date +%s)"

build_hash() {
  local site="$1" cents="$2" mref="$3" cref="$4" isrtc="$5"
  local notify="$6" bgid="$7" acct="$8" branch="$9" key="${10}"
  local raw="${site}${cents}${mref}${cref}${isrtc}${notify}${bgid}${acct}${branch}${key}"
  echo -n "$raw" | tr '[:upper:]' '[:lower:]' | openssl dgst -sha512 | awk '{print $2}'
}

echo "=== TC3/TC4/TC5: Successful payout (R10) ==="
echo "VERIFY_URL=$VERIFY_URL"
echo ""

REF="SX-CERT-OK-${TS}"
CENTS=1000
CREF="$(echo "$REF" | tr -cd '[:alnum:] -' | cut -c1-20)"
HASH=$(build_hash "$SITE_CODE" "$CENTS" "$REF" "$CREF" "false" \
  "$NOTIFY_URL" "$BANK_GROUP_ID" "$ACCOUNT_NUMBER" "$BRANCH_CODE" "$API_KEY")

echo "Submitting payout ref=$REF..."
RESP=$(curl -sS --max-time 30 -X POST "$BASE_URL/requestpayout" \
  -H "SiteCode: $SITE_CODE" \
  -H "ApiKey: $API_KEY" \
  -H "Content-Type: application/json" \
  -d "$(jq -n \
    --arg sc "$SITE_CODE" \
    --arg mr "$REF" \
    --arg cr "$CREF" \
    --arg nu "$NOTIFY_URL" \
    --arg vu "$VERIFY_URL" \
    --arg bgid "$BANK_GROUP_ID" \
    --arg acct "$ACCOUNT_NUMBER" \
    --arg bc "$BRANCH_CODE" \
    --arg hc "$HASH" \
    '{
      siteCode: $sc, amount: 10.00, merchantReference: $mr,
      customerBankReference: $cr, isRtc: false,
      notifyUrl: $nu, verifyUrl: $vu,
      bankingDetails: { bankGroupId: $bgid, accountNumber: $acct, branchCode: $bc },
      hashCheck: $hc
    }')")

echo "requestpayout response:"
echo "$RESP" | jq .

PAYOUT_ID=$(echo "$RESP" | jq -r '.payoutId // empty')
STATUS=$(echo "$RESP" | jq -r '.payoutStatus.status // empty')

[ -n "$PAYOUT_ID" ] || { echo "FAIL: no payoutId — check hash/credentials" >&2; exit 1; }
echo ""
echo "payoutId=$PAYOUT_ID  status=$STATUS"
echo "Dashboard: https://staging.ozow.com/merchant/payouts/$PAYOUT_ID"
echo ""
echo "Waiting 120s for verify + complete webhooks..."
echo "(Watch your API terminal for: PayoutVerify RAW body)"

for i in $(seq 1 12); do
  sleep 10
  GET=$(curl -sS --max-time 15 \
    "$BASE_URL/getpayout?payoutId=$PAYOUT_ID" \
    -H "SiteCode: $SITE_CODE" -H "ApiKey: $API_KEY")
  S=$(echo "$GET" | jq -r '.payoutStatus.status // empty')
  SUB=$(echo "$GET" | jq -r '.payoutStatus.subStatus // empty')
  echo "  [${i}0s] status=$S subStatus=$SUB"
  if [ "$S" = "5" ]; then
    echo ""
    echo "PASS — TC3/TC4/TC5 complete. Final status=5 (Completed)"
    echo "$GET" | jq .
    exit 0
  fi
  if [ "$S" = "3" ] || [ "$S" = "4" ]; then
    echo ""
    echo "FAIL — payout cancelled/failed. status=$S subStatus=$SUB"
    echo "$GET" | jq .
    exit 1
  fi
done

echo ""
echo "Timeout — final poll:"
curl -sS --max-time 15 \
  "$BASE_URL/getpayout?payoutId=$PAYOUT_ID" \
  -H "SiteCode: $SITE_CODE" -H "ApiKey: $API_KEY" | jq .
echo ""
echo "Check dashboard and API logs manually."
echo "Dashboard: https://staging.ozow.com/merchant/payouts/$PAYOUT_ID"
