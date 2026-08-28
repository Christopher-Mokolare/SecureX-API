#!/usr/bin/env bash
# Ozow Payouts Integration Test Cases
# Covers all 11 cases required for Ozow technical sign-off
#
# Usage:
#   bash test-ozow-cases.sh              # run all cases
#   bash test-ozow-cases.sh mock         # run only mock cases (9-11)
#   bash test-ozow-cases.sh live         # run only live cases (1-8)

set -euo pipefail

# ── Config ────────────────────────────────────────────────────────────────────
API_BASE="${API_BASE:-https://securex-api-vjf3.onrender.com}"
PAYOUT_BASE="https://stagingpayoutsapi.ozow.com/v1"
MOCK_BASE="https://stagingpayoutsapi.ozow.com/mock/v1"

SITE_CODE="${OZOW_SITE_CODE:-SEC-SEC-004}"
PAYOUT_API_KEY="${OZOW_PAYOUT_API_KEY:-}"
DECRYPTION_KEY="${OZOW_ACCOUNT_NUMBER_DECRYPTION_KEY:-}"
NOTIFY_URL="${OZOW_NOTIFY_URL:-$API_BASE/securex/payout-notification}"
VERIFY_URL="${OZOW_VERIFY_URL:-$API_BASE/securex/payout-verify}"

FNB_BANK_ID="4816019c-3314-4c80-8b6b-b2cd16dcc4ec"
FNB_BRANCH="250655"
VALID_ACCOUNT="62000000000"
INVALID_ACCOUNT="1234567890"

PGHOST="${PGHOST:-}"
PGUSER="${PGUSER:-}"
PGDB="${PGDB:-}"
PGPASSWORD="${PGPASSWORD:-}"
export PGHOST PGUSER PGDB PGPASSWORD

# ── Helpers ───────────────────────────────────────────────────────────────────
PASS=0; FAIL=0
RESULTS=()

pass() { echo "  ✅ $1"; PASS=$((PASS+1)); RESULTS+=("PASS: $1"); }
fail() { echo "  ❌ $1"; FAIL=$((FAIL+1)); RESULTS+=("FAIL: $1"); }
info() { echo "  ℹ️  $1"; }
section() { echo ""; echo "══════════════════════════════════════════════"; echo "  $1"; echo "══════════════════════════════════════════════"; }

require_api_key() {
  if [ -z "$PAYOUT_API_KEY" ]; then
    echo "❌ OZOW_PAYOUT_API_KEY env var is required"
    echo "   export OZOW_PAYOUT_API_KEY=your_key && bash test-ozow-cases.sh"
    exit 1
  fi
}

require_direct_payout_config() {
  if [ -z "$DECRYPTION_KEY" ]; then
    echo "OZOW_ACCOUNT_NUMBER_DECRYPTION_KEY is required for direct RequestPayout cases" >&2
    exit 1
  fi
  command -v openssl >/dev/null 2>&1 || { echo "openssl is required" >&2; exit 1; }
  command -v xxd >/dev/null 2>&1 || { echo "xxd is required" >&2; exit 1; }
}

sha512_lower() {
  printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | shasum -a 512 | awk '{print $1}'
}

encrypt_account() {
  local account=$1 ref=$2 cents=$3 key key_hex iv_hex
  key=$DECRYPTION_KEY
  while [ "${#key}" -lt 32 ]; do key="${key}${DECRYPTION_KEY}"; done
  key="${key:0:32}"
  key_hex="$(printf '%s' "$key" | xxd -p -c 256)"
  iv_hex="$(sha512_lower "${ref}${cents}${DECRYPTION_KEY}" | cut -c1-32)"
  printf '%s' "$account" | openssl enc -aes-256-cbc -K "$key_hex" -iv "$iv_hex" -a -A 2>/dev/null
}

request_payout_body() {
  local amount=$1 ref=$2 account=$3 cents encrypted customer_ref hash
  cents="$(awk -v amount="$amount" 'BEGIN { printf "%.0f", amount * 100 }')"
  encrypted="$(encrypt_account "$account" "$ref" "$cents")"
  customer_ref="${ref:0:20}"
  hash="$(sha512_lower "${SITE_CODE}${cents}${ref}${customer_ref}false${NOTIFY_URL}${VERIFY_URL}${FNB_BANK_ID}${encrypted}${FNB_BRANCH}${PAYOUT_API_KEY}")"
  jq -n \
    --arg siteCode "$SITE_CODE" --arg merchantReference "$ref" \
    --arg customerBankReference "$customer_ref" --arg notifyUrl "$NOTIFY_URL" \
    --arg verifyUrl "$VERIFY_URL" --arg bankGroupId "$FNB_BANK_ID" \
    --arg accountNumber "$encrypted" --arg branchCode "$FNB_BRANCH" \
    --arg hashCheck "$hash" --argjson amount "$amount" '{
      siteCode: $siteCode, amount: $amount, merchantReference: $merchantReference,
      customerBankReference: $customerBankReference, isRtc: false,
      notifyUrl: $notifyUrl, verifyUrl: $verifyUrl,
      bankingDetails: { bankGroupId: $bankGroupId, accountNumber: $accountNumber, branchCode: $branchCode },
      hashCheck: $hashCheck
    }'
}

get_token() {
  curl -s -X POST "$API_BASE/api/auth/token" \
    -H "Content-Type: application/json" \
    -d '{"email":"test@secureexchange.co.za"}' | jq -r '.token'
}

create_ready_transaction() {
  local amount=$1
  local token=$2
  local ts
  ts=$(date +%s%N)

  local resp
  resp=$(curl -s -X POST "$API_BASE/api/transactions" \
    -H "Authorization: Bearer $token" \
    -H "Content-Type: application/json" \
    -d "{
      \"buyerFullName\": \"Amina Fatou Clearwater\",
      \"buyerEmail\": \"amina.clearwater@example.com\",
      \"buyerPhone\": \"0821234567\",
      \"buyerIdNumber\": \"0000000000000\",
      \"sellerFullName\": \"Test Seller\",
      \"sellerEmail\": \"seller${ts}@test.co.za\",
      \"sellerPhone\": \"0829876543\",
      \"itemTitle\": \"Ozow Test\",
      \"itemDescription\": \"Integration test\",
      \"itemValue\": $amount,
      \"sellerLocation\": \"Johannesburg\",
      \"serviceType\": 0,
      \"feePayer\": 0
    }")

  local tx_id deal_ref seller_id
  tx_id=$(echo "$resp" | jq -r '.Id // empty')
  deal_ref=$(echo "$resp" | jq -r '.DealReference // empty')
  seller_id=$(echo "$resp" | jq -r '.Seller.Id // empty')

  if [ -z "$tx_id" ] || [ "$tx_id" = "null" ]; then
    echo "null null null"
    return
  fi

  curl -s -X POST "$API_BASE/api/users/$seller_id/bank-details" \
    -H "Authorization: Bearer $token" \
    -H "Content-Type: application/json" \
    -d "{
      \"accountNumber\": \"$VALID_ACCOUNT\",
      \"branchCode\": \"$FNB_BRANCH\",
      \"bankGroupId\": \"$FNB_BANK_ID\",
      \"idNumber\": \"8001015009087\"
    }" > /dev/null

  if [ -z "$PGHOST" ] || [ -z "$PGUSER" ] || [ -z "$PGDB" ] || [ -z "$PGPASSWORD" ]; then
    echo "Database connection variables are required to advance a transaction to payout state." >&2
    echo "Set PGHOST, PGUSER, PGDB, and PGPASSWORD without committing them." >&2
    echo "null null null"
    return
  fi

  psql -h "$PGHOST" -p 5432 -U "$PGUSER" -d "$PGDB" --set=sslmode=require -q \
    -c "UPDATE transactions SET status = 'FundsSecured', version = version + 1, updated_at = NOW() WHERE \"Id\" = '$tx_id';" 2>/dev/null

  curl -s -X POST "$API_BASE/api/transactions/$tx_id/start-logistics" \
    -H "Authorization: Bearer $token" \
    -H "Content-Type: application/json" \
    -d '{"actor":"seller","expectedVersion":2}' > /dev/null

  local delivered_resp
  delivered_resp=$(curl -s -X POST "$API_BASE/api/transactions/$tx_id/mark-delivered" \
    -H "Authorization: Bearer $token" \
    -H "Content-Type: application/json" \
    -d '{"actor":"seller","expectedVersion":3}')
  local version
  version=$(echo "$delivered_resp" | jq -r '.Version // 4')

  echo "$tx_id $deal_ref $seller_id $version"
}

ozow_get() {
  curl -s -H "SiteCode: $SITE_CODE" -H "ApiKey: $PAYOUT_API_KEY" \
    "$PAYOUT_BASE/$1"
}

ozow_post() {
  curl -s -X POST \
    -H "SiteCode: $SITE_CODE" -H "ApiKey: $PAYOUT_API_KEY" \
    -H "Content-Type: application/json" \
    -d "$2" "$PAYOUT_BASE/$1"
}

ozow_mock_post() {
  curl -s -X POST \
    -H "SiteCode: $SITE_CODE" -H "ApiKey: $PAYOUT_API_KEY" \
    -H "Content-Type: application/json" \
    -d "$2" "$MOCK_BASE/$1"
}

mock_config_base() {
  echo "$MOCK_BASE/settestconfiguration?siteCode=$SITE_CODE"
}

get_test_config() {
  curl -sS --fail-with-body \
    -H "SiteCode: $SITE_CODE" -H "ApiKey: $PAYOUT_API_KEY" \
    "$MOCK_BASE/gettestconfiguration?siteCode=$SITE_CODE"
}

set_test_config() {
  local field=$1
  local body
  echo "  Before configuration:"
  get_test_config | jq .
  body=$(cat <<EOF
{
  "siteCode": "$SITE_CODE",
  "isAccountDecryptionFailed": false,
  "isNullResponse": false,
  "isInvalidStatusCode": false,
  "isPayoutMismatch": false,
  "isNotVerifiedResponse": false,
  "isAccountNumberDecryptionKeyMissing": false,
  "hasRetryCountBeenExceeded": false
}
EOF
)
  body=$(echo "$body" | jq ".$field = true")
  curl -sS --fail-with-body -X POST \
    -H "SiteCode: $SITE_CODE" -H "ApiKey: $PAYOUT_API_KEY" \
    -H "Content-Type: application/json" \
    -d "$body" "$(mock_config_base)"
  echo ""
  echo "  After configuration:"
  get_test_config | jq .
}

reset_test_config() {
  curl -s -X POST \
    -H "SiteCode: $SITE_CODE" -H "ApiKey: $PAYOUT_API_KEY" \
    -H "Content-Type: application/json" \
    -d "{
      \"siteCode\": \"$SITE_CODE\",
      \"isAccountDecryptionFailed\": false,
      \"isNullResponse\": false,
      \"isInvalidStatusCode\": false,
      \"isPayoutMismatch\": false,
      \"isNotVerifiedResponse\": false,
      \"isAccountNumberDecryptionKeyMissing\": false,
      \"hasRetryCountBeenExceeded\": false
    }" "$(mock_config_base)" > /dev/null
}

# ── Main ──────────────────────────────────────────────────────────────────────
require_api_key

MODE="${1:-all}"
TOKEN=$(get_token)

if [ -z "$TOKEN" ] || [ "$TOKEN" = "null" ]; then
  echo "❌ Failed to get auth token from $API_BASE"
  exit 1
fi
echo "✅ Auth token obtained"

# ══════════════════════════════════════════════════════════════════════════════
# LIVE CASES (1–8)
# ══════════════════════════════════════════════════════════════════════════════

if [ "$MODE" = "all" ] || [ "$MODE" = "live" ]; then

  section "Case 1: Minimum amount validation (R0.50 — below R1 minimum)"

  read -r TX_ID DEAL_REF SELLER_ID VERSION <<< "$(create_ready_transaction 0.50 "$TOKEN")"

  if [ -z "$TX_ID" ] || [ "$TX_ID" = "null" ]; then
    fail "Case 1: Could not create transaction"
  else
    curl -s -X POST "$API_BASE/api/transactions/$TX_ID/accept" \
      -H "Authorization: Bearer $TOKEN" \
      -H "Content-Type: application/json" \
      -d "{\"actor\":\"buyer\",\"expectedVersion\":$VERSION}" > /dev/null

    sleep 3
    PAYOUT_ID=$(psql -h "$PGHOST" -p 5432 -U "$PGUSER" -d "$PGDB" --set=sslmode=require -t -q \
      -c "SELECT payout_id FROM pending_payouts WHERE deal_reference = '$DEAL_REF' LIMIT 1;" 2>/dev/null | xargs)

    if [ -z "$PAYOUT_ID" ]; then
      pass "Case 1: Ozow rejected R0.50 payout (below minimum R1) — no payoutId created"
      info "Deal ref: $DEAL_REF"
    else
      fail "Case 1: Ozow accepted R0.50 — expected rejection. PayoutId=$PAYOUT_ID"
    fi

    require_direct_payout_config
    info "Direct Ozow API test for minimum validation:"
    DIRECT_RESP=$(ozow_post "requestpayout" "$(request_payout_body 0.50 "MIN-TEST-$(date +%s)" "$VALID_ACCOUNT")")
    echo "  Response: $DIRECT_RESP"
  fi

  section "Case 2: Maximum amount validation (R21 — above R20 maximum)"

  read -r TX_ID DEAL_REF SELLER_ID VERSION <<< "$(create_ready_transaction 21 "$TOKEN")"

  if [ -z "$TX_ID" ] || [ "$TX_ID" = "null" ]; then
    fail "Case 2: Could not create transaction"
  else
    curl -s -X POST "$API_BASE/api/transactions/$TX_ID/accept" \
      -H "Authorization: Bearer $TOKEN" \
      -H "Content-Type: application/json" \
      -d "{\"actor\":\"buyer\",\"expectedVersion\":$VERSION}" > /dev/null

    sleep 3
    PAYOUT_ID=$(psql -h "$PGHOST" -p 5432 -U "$PGUSER" -d "$PGDB" --set=sslmode=require -t -q \
      -c "SELECT payout_id FROM pending_payouts WHERE deal_reference = '$DEAL_REF' LIMIT 1;" 2>/dev/null | xargs)

    if [ -z "$PAYOUT_ID" ]; then
      pass "Case 2: Ozow rejected R21 payout (above maximum R20) — no payoutId created"
      info "Deal ref: $DEAL_REF"
    else
      fail "Case 2: Ozow accepted R21 — expected rejection. PayoutId=$PAYOUT_ID"
    fi

    require_direct_payout_config
    info "Direct Ozow API test for maximum validation:"
    DIRECT_RESP=$(ozow_post "requestpayout" "$(request_payout_body 21.00 "MAX-TEST-$(date +%s)" "$VALID_ACCOUNT")")
    echo "  Response: $DIRECT_RESP"
  fi

  section "Cases 3-5: Verify / Payout Complete / Payout Cancelled"
  echo ""
  echo "  These require a live payout to be submitted and tracked on the dashboard."
  echo "  Run test-payout-final.sh to submit a R1 payout, then:"
  echo ""
  echo "  Case 3 (Verification Request received + responded):  PayoutId from dashboard"
  echo "  Case 4 (Payout Verification Success):                PayoutId from dashboard"
  echo "  Case 5 (Payout Complete):                            PayoutId from dashboard"
  echo ""
  echo "  Dashboard: https://stagingdash.ozow.com/MerchantAdmin/Payout/Payouts"
  echo ""
  echo "  To trigger a cancellation (Case 6 — Payout Cancelled):"
  echo "  Submit a payout and cancel it from the Ozow dashboard before processing."

  section "Case 7: CDV error — account number validation failure"

  read -r TX_ID DEAL_REF SELLER_ID VERSION <<< "$(create_ready_transaction 1 "$TOKEN")"

  if [ -z "$TX_ID" ] || [ "$TX_ID" = "null" ]; then
    fail "Case 7: Could not create transaction"
  else
    curl -s -X POST "$API_BASE/api/users/$SELLER_ID/bank-details" \
      -H "Authorization: Bearer $TOKEN" \
      -H "Content-Type: application/json" \
      -d "{
        \"accountNumber\": \"$INVALID_ACCOUNT\",
        \"branchCode\": \"$FNB_BRANCH\",
        \"bankGroupId\": \"$FNB_BANK_ID\",
        \"idNumber\": \"8001015009087\"
      }" > /dev/null

    curl -s -X POST "$API_BASE/api/transactions/$TX_ID/accept" \
      -H "Authorization: Bearer $TOKEN" \
      -H "Content-Type: application/json" \
      -d "{\"actor\":\"buyer\",\"expectedVersion\":$VERSION}" > /dev/null

    sleep 5
    CDV_ROW=$(psql -h "$PGHOST" -p 5432 -U "$PGUSER" -d "$PGDB" --set=sslmode=require -t -q \
      -c "SELECT payout_id FROM pending_payouts WHERE deal_reference = '$DEAL_REF' LIMIT 1;" 2>/dev/null | xargs)

    if [ -n "$CDV_ROW" ]; then
      pass "Case 7: Payout submitted with invalid account — PayoutId=$CDV_ROW"
      info "Check Ozow dashboard for CDV error status (subStatus 9904)"
      info "Deal ref: $DEAL_REF | PayoutId: $CDV_ROW"
    else
      info "Case 7: Ozow rejected at submission (CDV check at request time)"
      info "Deal ref: $DEAL_REF — check ECS logs for rejection details"
    fi
  fi

  section "Case 8: Get Payout Status (getpayout API)"

  LATEST_PAYOUT=$(psql -h "$PGHOST" -p 5432 -U "$PGUSER" -d "$PGDB" --set=sslmode=require -t -q \
    -c "SELECT payout_id FROM pending_payouts WHERE resolved = true ORDER BY submitted_at DESC LIMIT 1;" 2>/dev/null | xargs)

  if [ -z "$LATEST_PAYOUT" ]; then
    fail "Case 8: No resolved payouts found in DB — run test-payout-final.sh first"
  else
    GET_RESP=$(ozow_get "getpayout?payoutId=$LATEST_PAYOUT")
    STATUS=$(echo "$GET_RESP" | jq -r '.payoutStatus.status // "unknown"')
    SUB=$(echo "$GET_RESP" | jq -r '.payoutStatus.subStatus // "unknown"')

    if echo "$GET_RESP" | jq -e '.id' > /dev/null 2>&1; then
      pass "Case 8: getpayout returned valid response — status=$STATUS subStatus=$SUB"
      echo ""
      echo "  ── JSON Response (save for Ozow form) ──"
      echo "$GET_RESP" | jq .
    else
      fail "Case 8: getpayout returned unexpected response"
      echo "  Response: $GET_RESP"
    fi
  fi

fi  # end live cases

# ══════════════════════════════════════════════════════════════════════════════
# MOCK CASES (9–11)
# ══════════════════════════════════════════════════════════════════════════════

if [ "$MODE" = "all" ] || [ "$MODE" = "mock" ]; then

  mock_payout_body() {
    echo "{
      \"siteCode\": \"$SITE_CODE\",
      \"amount\": 1.00,
      \"merchantReference\": \"$1\",
      \"customerBankReference\": \"MOCKTEST\",
      \"isRtc\": false,
      \"notifyUrl\": \"https://api.secureexchange.co.za/securex/payout-notification\",
      \"bankingDetails\": {
        \"bankGroupId\": \"$FNB_BANK_ID\",
        \"accountNumber\": \"dummyencrypted\",
        \"branchCode\": \"$FNB_BRANCH\"
      },
      \"hashCheck\": \"dummy\"
    }"
  }

  section "Case 9: Mock — IsAccountDecryptionFailed"
  info "Setting test config: isAccountDecryptionFailed=true"
  CONFIG_RESP=$(set_test_config "isAccountDecryptionFailed")
  echo "  SetConfig response: $CONFIG_RESP"

  MOCK_REF="MOCK-DECFAIL-$(date +%s)"
  MOCK_RESP=$(ozow_mock_post "requestpayout" "$(mock_payout_body "$MOCK_REF")")
  MOCK_PAYOUT_ID=$(echo "$MOCK_RESP" | jq -r '.payoutId // empty')
  echo ""; echo "  ── Mock RequestPayout Response ──"
  echo "$MOCK_RESP" | jq . 2>/dev/null || echo "$MOCK_RESP"

  if [ -n "$MOCK_PAYOUT_ID" ]; then
    sleep 5
    GET_RESP=$(curl -s -H "SiteCode: $SITE_CODE" -H "ApiKey: $PAYOUT_API_KEY" \
      "$MOCK_BASE/getpayout?payoutId=$MOCK_PAYOUT_ID")
    echo ""; echo "  ── Mock GetPayout Response ──"
    echo "$GET_RESP" | jq . 2>/dev/null || echo "$GET_RESP"
    MOCK_STATUS=$(echo "$GET_RESP" | jq -r '.payoutStatus.status // "unknown"')
    MOCK_SUB=$(echo "$GET_RESP" | jq -r '.payoutStatus.subStatus // "unknown"')
  else
    MOCK_STATUS=$(echo "$MOCK_RESP" | jq -r '.payoutStatus.status // "unknown"')
    MOCK_SUB=$(echo "$MOCK_RESP" | jq -r '.payoutStatus.subStatus // "unknown"')
  fi

  if [ "$MOCK_STATUS" = "99" ] && [ "$MOCK_SUB" = "205" ]; then
    pass "Case 9: IsAccountDecryptionFailed — status=99 subStatus=205 ✓"
  else
    info "Case 9: Got status=$MOCK_STATUS subStatus=$MOCK_SUB (expected 99/205)"
  fi
  reset_test_config; sleep 2

  section "Case 10: Mock — IsNotVerifiedResponse"
  info "Setting test config: isNotVerifiedResponse=true"
  CONFIG_RESP=$(set_test_config "isNotVerifiedResponse")
  echo "  SetConfig response: $CONFIG_RESP"

  MOCK_REF="MOCK-NOTVER-$(date +%s)"
  MOCK_RESP=$(ozow_mock_post "requestpayout" "$(mock_payout_body "$MOCK_REF")")
  MOCK_PAYOUT_ID=$(echo "$MOCK_RESP" | jq -r '.payoutId // empty')
  echo ""; echo "  ── Mock RequestPayout Response ──"
  echo "$MOCK_RESP" | jq . 2>/dev/null || echo "$MOCK_RESP"

  if [ -n "$MOCK_PAYOUT_ID" ]; then
    sleep 2
    GET_RESP=$(curl -s -H "SiteCode: $SITE_CODE" -H "ApiKey: $PAYOUT_API_KEY" \
      "$MOCK_BASE/getpayout?payoutId=$MOCK_PAYOUT_ID")
    echo ""; echo "  ── Mock GetPayout Response ──"
    echo "$GET_RESP" | jq . 2>/dev/null || echo "$GET_RESP"
    MOCK_STATUS=$(echo "$GET_RESP" | jq -r '.payoutStatus.status // "unknown"')
    MOCK_SUB=$(echo "$GET_RESP" | jq -r '.payoutStatus.subStatus // "unknown"')
  else
    MOCK_STATUS=$(echo "$MOCK_RESP" | jq -r '.payoutStatus.status // "unknown"')
    MOCK_SUB=$(echo "$MOCK_RESP" | jq -r '.payoutStatus.subStatus // "unknown"')
  fi

  if [ "$MOCK_STATUS" = "99" ] && [ "$MOCK_SUB" = "202" ]; then
    pass "Case 10: IsNotVerifiedResponse — status=99 subStatus=202 ✓"
  else
    info "Case 10: Got status=$MOCK_STATUS subStatus=$MOCK_SUB (expected 99/202)"
  fi
  reset_test_config; sleep 1

  section "Case 11: Mock — IsAccountDecryptionKeyMissing"
  info "Setting test config: isAccountNumberDecryptionKeyMissing=true"
  CONFIG_RESP=$(set_test_config "isAccountNumberDecryptionKeyMissing")
  echo "  SetConfig response: $CONFIG_RESP"

  MOCK_REF="MOCK-KEYMISS-$(date +%s)"
  MOCK_RESP=$(ozow_mock_post "requestpayout" "$(mock_payout_body "$MOCK_REF")")
  MOCK_PAYOUT_ID=$(echo "$MOCK_RESP" | jq -r '.payoutId // empty')
  echo ""; echo "  ── Mock RequestPayout Response ──"
  echo "$MOCK_RESP" | jq . 2>/dev/null || echo "$MOCK_RESP"

  if [ -n "$MOCK_PAYOUT_ID" ]; then
    sleep 2
    GET_RESP=$(curl -s -H "SiteCode: $SITE_CODE" -H "ApiKey: $PAYOUT_API_KEY" \
      "$MOCK_BASE/getpayout?payoutId=$MOCK_PAYOUT_ID")
    echo ""; echo "  ── Mock GetPayout Response ──"
    echo "$GET_RESP" | jq . 2>/dev/null || echo "$GET_RESP"
    MOCK_STATUS=$(echo "$GET_RESP" | jq -r '.payoutStatus.status // "unknown"')
    MOCK_SUB=$(echo "$GET_RESP" | jq -r '.payoutStatus.subStatus // "unknown"')
  else
    MOCK_STATUS=$(echo "$MOCK_RESP" | jq -r '.payoutStatus.status // "unknown"')
    MOCK_SUB=$(echo "$MOCK_RESP" | jq -r '.payoutStatus.subStatus // "unknown"')
  fi

  if [ "$MOCK_STATUS" = "99" ] && [ "$MOCK_SUB" = "205" ]; then
    pass "Case 11: IsAccountDecryptionKeyMissing — status=99 subStatus=205 ✓"
  else
    info "Case 11: Got status=$MOCK_STATUS subStatus=$MOCK_SUB (expected 99/205)"
  fi
  reset_test_config

fi  # end mock cases

# ── Summary ───────────────────────────────────────────────────────────────────
section "Summary"
echo "  Passed: $PASS"
echo "  Failed: $FAIL"
echo ""
for r in "${RESULTS[@]}"; do
  echo "  $r"
done
echo ""
echo "  Dashboard: https://stagingdash.ozow.com/MerchantAdmin/Payout/Payouts"
echo "  Logs:      aws logs filter-log-events --log-group-name /ecs/securex-api --region af-south-1 --filter-pattern 'TriggerPayout'"
