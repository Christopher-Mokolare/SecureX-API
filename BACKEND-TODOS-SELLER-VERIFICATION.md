# P0 — Seller verification is broken end-to-end

**Discovered:** 2026-09-14

## Symptom

Seller can progress deals (mark-as-shipped, confirm-delivery) without ever
completing biometric liveness. FE shows action buttons based on transaction
status only, not on seller LivenessStatus. BE does not gate either.

## Root cause — two parts

### 1. `id_selection` sent as string, not object

SmileID's Web SDK expects:

We send the literal string `"false"` (from cfg["SmileId:IdSelection"]).

Result: the "Select ID Type" country dropdown renders empty in the v12
widget. No seller has ever completed biometric KYC.

Confirmed by SmileID support 2026-09-14 (ticket thread with Quienzy Ong'eye).

### 2. Seller-liveness webhook misrouted as buyer-KYC

When a webhook arrives without `partner_params.verification_type == "seller_liveness"`,
it falls through to the buyer branch and sets `IdCheckStatus = Approved`
instead of `LivenessStatus = Approved`.

Evidence:
- Karabo's row: IdCheckStatus=Approved, LivenessStatus=Pending, has_kyc_job=t
- Same pattern on every seller with a KYC job

## Affected files

- SecureX.Api/Services/SmileIdService.cs — MintTokenAsync, id_selection
- SecureX.Api/Controllers/TransactionsController.cs — StartSellerKyc response
- src/app/pages/bank-details/bank-details.ts — launchSmileIdSdk, id_selection
- SecureX.Api/Webhooks/SmileIdWebhookController.cs — seller vs buyer routing

## Fixes required (in order)

1. [ ] Send id_selection as JSON object in MintTokenAsync
2. [ ] Pass id_selection through StartSellerKyc response
3. [ ] Send id_selection as object from bank-details.ts to SDK
4. [ ] Add raw-body logging to KycWebhook to capture actual webhook shape
5. [ ] Add fallback routing in KycWebhook: if verification_type null, match on
       partner_params.job_id against User.SmileIdJobId + Transaction context
6. [ ] BE gate: MarkAsShipped, ConfirmDelivery reject unless
       seller.LivenessStatus == Approved (403)
7. [ ] FE gate: seller page hides action buttons unless
       tx.seller.livenessStatus === 'Approved'
8. [ ] Backfill: set Karabo's IdCheckStatus back to Pending
9. [ ] Full retest of seller flow

## Evidence

- DB query 2026-09-14: users table shows 5+ rows with IdCheckStatus=Approved
  but LivenessStatus=Pending
- SmileID support thread (id_selection format confirmation)
