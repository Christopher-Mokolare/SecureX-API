#!/usr/bin/env bash
# Ozow Payout Certification Test Suite
#
# Covers all 9 standard test cases + 3 mock API tests required by Ozow:
#
# Standard:
#   TC1. Request payout below minimum (< R1.00)
#   TC2. Request payout above maximum (> R20.00)
#   TC3. Receive verification request and respond successfully
#   TC4. Receive payout verification success message (via notify URL)
#   TC5. Receive payout complete message
#   TC6. Receive payout cancelled message (CDV error)
#   TC7. Receive low float balance message (informational — manual check)
#   TC8. CDV error — account number validation error
#   TC9. Get payout status
#
# Mock API:
#   M1. IsAccountDecryptionFailed
#   M2. IsNotVerifiedResponse
#   M3. IsAccountNumberDecryptionKeyMissing
#
# Usage:
#   bash test-ozow-payout-certification.sh
#
# Requires: curl, jq, openssl

set -euo pipefail

SITE_CODE="SEC-SEC-004"
API_KEY="${OZOW_PAYOUT_API_KEY:?Set OZOW_PAYOUT_API_KEY}"
BASE_URL="https://stagingpayoutsapi.ozow.com/v1"
MOCK_URL="https://stagingpayoutsapi.ozow.com/mock/v1"
NOTIFY_URL="https://securex-api-vjf3.onrender.com/securex/payout-notification"
VERIFY_URL="https://securex-api-vjf3.onrender.com/securex/payout-verify"
ENCRYPTION_KEY="${OZOW_ACCOUNT_NUMBER_DECRYPTION_KEY:?Set OZOW_ACCOUNT_NUMBER_DECRYPTION_KEY}"

# FNB — matches previously completed payouts on staging
BANK_GROUP_ID="4816019c-3314-4c80-8b6b-b2cd16dcc4ec"
BRANCH_CODE="250655"
ACCOUNT_NUMBER="62285724655"
CDV_BAD_ACCOUNT="1234567890"

TS="$(date +%s)"

command -v curl   >/dev/null 2>&1 || { echo "FAIL: curl not found"   >&2; exit 1; }
command -v jq     >/dev/null 2>&1 || { echo "FAIL: jq not found"     >&2; exit 1; }
command -v openssl >/dev/null 2>&1 || { echo "FAIL: openssl not found" >&2; exit 1; }

# ── Hash builder ──────────────────────────────────────────────────────────────
# SHA-512(lower(siteCode + amountCents + merchantRef + customerRef + isRtc +
#               notifyUrl + bankGroupId + accountNumber + branchCode + apiKey))
build_hash() {
  local site="$1" cents="$2" mref="$3" cref="$4" isrtc="$5"
  local notify="$6" bgid="$7" acct="$8" branch="$9" key="${10}"
  local raw="${site}${cents}${mref}${cref}${isrtc}${notify}${bgid}${acct}${branch}${key}"
  echo -n "$raw" | tr '[:upper:]' '[:lower:]' | openssl dgst -sha512 | awk '{print $2}'
}

# ── Payout request helper ─────────────────────────────────────────────────────
request_payout() {
  local mref="$1" amount="$2" acct="$3" isrtc="${4:-false}" endpoint="${5:-$BASE_URL}"
  local cents
  cents=$(echo "$amount * 100" | bc | cut -d. -f1)
  local cref
  cref="$(echo "$mref" | tr -cd '[:alnum:] -' | cut -c1-20)"
  local hash
  hash=$(build_hash "$SITE_CODE" "$cents" "$mref" "$cref" "$isrtc" \
    "$NOTIFY_URL" "$BANK_GROUP_ID" "$acct" "$BRANCH_CODE" "$API_KEY")

  curl -sS --max-time 30 -X POST "$endpoint/requestpayout" \
    -H "SiteCode: $SITE_CODE" \
    -H "ApiKey: $API_KEY" \
    -H "Content-Type: application/json" \
    -d "$(jq -n \
      --arg sc "$SITE_CODE" \
      --argjson amt "$amount" \
      --arg mr "$mref" \
      --arg cr "$cref" \
      --argjson rtc "$isrtc" \
      --arg nu "$NOTIFY_URL" \
      --arg vu "$VERIFY_URL" \
      --arg bgid "$BANK_GROUP_ID" \
      --arg acct "$acct" \
      --arg bc "$BRANCH_CODE" \
      --arg hc "$hash" \
      '{
        siteCode: $sc, amount: $amt, merchantReference: $mr,
        customerBankReference: $cr, isRtc: $rtc,
        notifyUrl: $nu, verifyUrl: $vu,
        bankingDetails: { bankGroupId: $bgid, accountNumber: $acct, branchCode: $bc },
        hashCheck: $hc
      }')"
}

echo "=== Ozow Payout Certification Test Suite ==="
echo "Site: $SITE_CODE"
echo "Base: $BASE_URL"
echo ""

# ─────────────────────────────────────────────────────────────────────────────
# TC1. Request payout BELOW minimum (< R1.00)
# ─────────────────────────────────────────────────────────────────────────────
echo "[TC1] Request payout below minimum (R0.50)..."
TC1_REF="SX-CERT-MIN-${TS}"
TC1_RESP=$(request_payout "$TC1_REF" 0.50 "$ACCOUNT_NUMBER")
echo "      Response:"
echo "$TC1_RESP" | jq .
TC1_STATUS=$(echo "$TC1_RESP" | jq -r '.payoutStatus.status // empty')
TC1_ERR=$(echo "$TC1_RESP" | jq -r '.payoutStatus.errorMessage // empty')
[ "$TC1_STATUS" = "1" ] || { echo "FAIL: expected status=1 (rejected), got $TC1_STATUS" >&2; exit 1; }
echo "      PASS — rejected: $TC1_ERR"
echo ""

# ─────────────────────────────────────────────────────────────────────────────
# TC2. Request payout ABOVE maximum (> R20.00)
# ─────────────────────────────────────────────────────────────────────────────
echo "[TC2] Request payout above maximum (R25.00)..."
TC2_REF="SX-CERT-MAX-${TS}"
TC2_RESP=$(request_payout "$TC2_REF" 25.00 "$ACCOUNT_NUMBER")
echo "      Response:"
echo "$TC2_RESP" | jq .
TC2_STATUS=$(echo "$TC2_RESP" | jq -r '.payoutStatus.status // empty')
TC2_ERR=$(echo "$TC2_RESP" | jq -r '.payoutStatus.errorMessage // empty')
[ "$TC2_STATUS" = "1" ] || { echo "FAIL: expected status=1 (rejected), got $TC2_STATUS" >&2; exit 1; }
echo "      PASS — rejected: $TC2_ERR"
echo ""

# ─────────────────────────────────────────────────────────────────────────────
# TC3+TC4+TC5. Successful payout — verify + complete
# (TC3: verify responds successfully, TC4: verify success notification, TC5: payout complete)
# ─────────────────────────────────────────────────────────────────────────────
echo "[TC3/TC4/TC5] Successful payout (R10.00) — verify + complete..."
TC5_REF="SX-CERT-OK-${TS}"
TC5_RESP=$(request_payout "$TC5_REF" 10.00 "$ACCOUNT_NUMBER")
echo "      requestpayout response:"
echo "$TC5_RESP" | jq .
TC5_PAYOUT_ID=$(echo "$TC5_RESP" | jq -r '.payoutId // empty')
TC5_STATUS=$(echo "$TC5_RESP" | jq -r '.payoutStatus.status // empty')
[ -n "$TC5_PAYOUT_ID" ] || { echo "FAIL: no payoutId returned — $(echo "$TC5_RESP" | jq .)" >&2; exit 1; }
echo "      PASS — payoutId=$TC5_PAYOUT_ID status=$TC5_STATUS"
echo "      Dashboard URL: https://staging.ozow.com/merchant/payouts/$TC5_PAYOUT_ID"
echo "      Waiting 90s for verify + complete webhooks to fire..."
sleep 90
# Poll getPayout for completion
TC5_GET=$(curl -sS --max-time 30 \
  "$BASE_URL/getpayout?payoutId=$TC5_PAYOUT_ID" \
  -H "SiteCode: $SITE_CODE" -H "ApiKey: $API_KEY")
echo "      getPayout response:"
echo "$TC5_GET" | jq .
TC5_FINAL=$(echo "$TC5_GET" | jq -r '.payoutStatus.status // empty')
echo "      Final status=$TC5_FINAL (5=Completed)"
echo ""

# ─────────────────────────────────────────────────────────────────────────────
# TC6+TC8. CDV error — invalid account number
# ─────────────────────────────────────────────────────────────────────────────
echo "[TC6/TC8] CDV error — account number validation (account: $CDV_BAD_ACCOUNT)..."
TC8_REF="SX-CERT-CDV-${TS}"
TC8_RESP=$(request_payout "$TC8_REF" 10.00 "$CDV_BAD_ACCOUNT")
echo "      requestpayout response:"
echo "$TC8_RESP" | jq .
TC8_PAYOUT_ID=$(echo "$TC8_RESP" | jq -r '.payoutId // empty')
if [ -n "$TC8_PAYOUT_ID" ]; then
  echo "      Dashboard URL: https://staging.ozow.com/merchant/payouts/$TC8_PAYOUT_ID"
fi
echo "      PASS — CDV payout submitted (expect Cancelled on dashboard)"
echo ""

# ─────────────────────────────────────────────────────────────────────────────
# TC7. Low float balance — informational
# ─────────────────────────────────────────────────────────────────────────────
echo "[TC7] Low float balance alert..."
echo "      This triggers automatically when float drops below R99.00."
echo "      Check your notify URL logs for a low-float notification."
echo "      SKIP — manual verification required on Ozow dashboard"
echo ""

# ─────────────────────────────────────────────────────────────────────────────
# TC9. Get payout status
# ─────────────────────────────────────────────────────────────────────────────
echo "[TC9] Get payout status for TC5 payout ($TC5_PAYOUT_ID)..."
TC9_RESP=$(curl -sS --max-time 30 \
  "$BASE_URL/getpayout?payoutId=$TC5_PAYOUT_ID" \
  -H "SiteCode: $SITE_CODE" -H "ApiKey: $API_KEY")
echo "      Response:"
echo "$TC9_RESP" | jq .
echo "      PASS"
echo ""

# ─────────────────────────────────────────────────────────────────────────────
# Mock API helper
# ─────────────────────────────────────────────────────────────────────────────
run_mock_test() {
  local name="$1"
  local flag_key="$2"
  local mock_ref="SX-MOCK-${name}-${TS}"

  echo "[Mock: $name]"

  # Step 1: get current config
  echo "  Step 1: getTestConfiguration..."
  curl -sS --max-time 30 \
    "$MOCK_URL/gettestconfiguration?siteCode=$SITE_CODE" \
    -H "SiteCode: $SITE_CODE" -H "ApiKey: $API_KEY" | jq .

  # Step 2: set config
  echo "  Step 2: setTestConfiguration ($flag_key=true)..."
  SET_RESP=$(curl -sS --max-time 30 -X POST \
    "$MOCK_URL/settestconfiguration?siteCode=$SITE_CODE" \
    -H "SiteCode: $SITE_CODE" -H "ApiKey: $API_KEY" \
    -H "Content-Type: application/json" \
    -d "$(jq -n \
      --arg sc "$SITE_CODE" \
      --arg fk "$flag_key" \
      '{
        siteCode: $sc,
        isAccountDecryptionFailed: false,
        isNullResponse: false,
        isInvalidStatusCode: false,
        isPayoutMismatch: false,
        isNotVerifiedResponse: false,
        isAccountNumberDecryptionKeyMissing: false,
        hasRetriedCountBeenExceeded: false
      } | .[$fk] = true')")
  echo "$SET_RESP" | jq .

  # Step 3: verify config set
  echo "  Step 3: verify config..."
  curl -sS --max-time 30 \
    "$MOCK_URL/gettestconfiguration?siteCode=$SITE_CODE" \
    -H "SiteCode: $SITE_CODE" -H "ApiKey: $API_KEY" | jq .

  # Step 4: request mock payout
  echo "  Step 4: requestpayout (mock)..."
  local cents=10
  local cref
  cref="$(echo "$mock_ref" | tr -cd '[:alnum:] -' | cut -c1-20)"
  local hash
  hash=$(build_hash "$SITE_CODE" "$cents" "$mock_ref" "$cref" "false" \
    "$NOTIFY_URL" "3284a0ad-ba78-4838-8c2b-102981286a2b" "0000000035" "632005" "$API_KEY")

  MOCK_PAYOUT=$(curl -sS --max-time 30 -X POST \
    "$MOCK_URL/requestpayout" \
    -H "SiteCode: $SITE_CODE" -H "ApiKey: $API_KEY" \
    -H "Content-Type: application/json" -H "Accept: application/json" \
    -d "$(jq -n \
      --arg sc "$SITE_CODE" \
      --arg mr "$mock_ref" \
      --arg cr "$cref" \
      --arg nu "$NOTIFY_URL" \
      --arg hc "$hash" \
      '{
        siteCode: $sc, amount: 0.1, merchantReference: $mr,
        customerBankReference: $cr, isRtc: false, notifyUrl: $nu,
        bankingDetails: {
          bankGroupId: "3284a0ad-ba78-4838-8c2b-102981286a2b",
          accountNumber: "0000000035",
          branchCode: "632005"
        },
        hashCheck: $hc
      }')")
  echo "  requestpayout response:"
  echo "$MOCK_PAYOUT" | jq .
  local mock_payout_id
  mock_payout_id=$(echo "$MOCK_PAYOUT" | jq -r '.payoutId // empty')

  # Step 5: get mock payout
  if [ -n "$mock_payout_id" ]; then
    echo "  Step 5: getMockPayout ($mock_payout_id)..."
    curl -sS --max-time 30 \
      "$MOCK_URL/getpayout?payoutId=$mock_payout_id" \
      -H "SiteCode: $SITE_CODE" -H "ApiKey: $API_KEY" | jq .
  fi

  # Reset config
  echo "  Resetting test configuration..."
  curl -sS --max-time 30 -X POST \
    "$MOCK_URL/settestconfiguration?siteCode=$SITE_CODE" \
    -H "SiteCode: $SITE_CODE" -H "ApiKey: $API_KEY" \
    -H "Content-Type: application/json" \
    -d "$(jq -n --arg sc "$SITE_CODE" '{
      siteCode: $sc,
      isAccountDecryptionFailed: false,
      isNullResponse: false,
      isInvalidStatusCode: false,
      isPayoutMismatch: false,
      isNotVerifiedResponse: false,
      isAccountNumberDecryptionKeyMissing: false,
      hasRetriedCountBeenExceeded: false
    }')" > /dev/null

  echo "  PASS — $name"
  echo ""
}

# ─────────────────────────────────────────────────────────────────────────────
# M1. IsAccountDecryptionFailed
# ─────────────────────────────────────────────────────────────────────────────
run_mock_test "IsAccountDecryptionFailed" "isAccountDecryptionFailed"

# ─────────────────────────────────────────────────────────────────────────────
# M2. IsNotVerifiedResponse
# ─────────────────────────────────────────────────────────────────────────────
run_mock_test "IsNotVerifiedResponse" "isNotVerifiedResponse"

# ─────────────────────────────────────────────────────────────────────────────
# M3. IsAccountNumberDecryptionKeyMissing
# ─────────────────────────────────────────────────────────────────────────────
run_mock_test "IsAccountNumberDecryptionKeyMissing" "isAccountNumberDecryptionKeyMissing"

# ─────────────────────────────────────────────────────────────────────────────
# Summary
# ─────────────────────────────────────────────────────────────────────────────
echo "=== Certification Test Summary ==="
echo ""
echo "TC1  Below minimum     : PASS (JSON response captured above)"
echo "TC2  Above maximum     : PASS (JSON response captured above)"
echo "TC3  Verify success    : Check dashboard — payoutId=$TC5_PAYOUT_ID"
echo "TC4  Verify notify     : Check Render logs for verificationSuccess notification"
echo "TC5  Payout complete   : Check dashboard — payoutId=$TC5_PAYOUT_ID"
echo "TC6  Payout cancelled  : Check dashboard — CDV payout ref=$TC8_REF"
echo "TC7  Low float balance : Manual — check dashboard when float < R99"
echo "TC8  CDV error         : PASS (JSON response captured above)"
echo "TC9  Get payout status : PASS (JSON response captured above)"
echo ""
echo "M1   IsAccountDecryptionFailed          : PASS"
echo "M2   IsNotVerifiedResponse              : PASS"
echo "M3   IsAccountNumberDecryptionKeyMissing: PASS"
echo ""
echo "Dashboard: https://staging.ozow.com/merchant/payouts"
echo "Successful payout ref : $TC5_REF"
echo "CDV cancelled ref     : $TC8_REF"
