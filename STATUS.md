# SecureX Backend — Development Status Report
*Last updated: 2 July 2026*

---

## Overall Progress: ~75% Complete

The core escrow flow is fully built and tested end to end. The remaining 25% is split between third-party integrations (KYC, comms) and a few outstanding business decisions.

---

## 1. Infrastructure ✅

| Item | Status |
|---|---|
| ASP.NET Core 8 API | ✅ Done |
| Supabase PostgreSQL connected | ✅ Done |
| All 9 tables live in DB | ✅ Done |
| EF Core migrations | ✅ Done |
| Auto-migrates on startup | ✅ Done |
| CORS configured | ✅ Done |
| Health endpoint `/health` | ✅ Done |
| Swagger UI (dev) | ✅ Done |
| Deploy C# API to Render | ❌ Not done — old Node.js still live |

---

## 2. Ozow Webhooks ✅

| Endpoint | Status | Notes |
|---|---|---|
| `POST /securex/payout-verify` | ✅ Done | Hash validation, returns `accountNumberDecryptionKey` |
| `POST /securex/payout-notification` | ✅ Done | Hash validation, idempotency, duplicate detection |
| `AccessToken` header auth | ✅ Done | Guards all `/securex/*` endpoints |

---

## 3. Transaction Lifecycle ✅

### State Machine
```
Initialized
  → BuyerKycPending
    → BuyerKycFailed
    → PaymentPending
      → FundsSecured
        → SellerKycPending
          → SellerKycFailed → RequiresRefund
          → LogisticsPending
            → ItemDelivered (24hr inspection window starts)
              → Completed → TriggerPayout ✅
              → RequiresRefund (buyer rejected within 24hrs — funds held in float)
```

| Feature | Status | Notes |
|---|---|---|
| State machine | ✅ Done | All 11 states implemented |
| Row locking + optimistic concurrency | ✅ Done | Prevents race conditions |
| 24hr inspection window | ✅ Done | Enforced on reject |
| Funds held in float on reject | ✅ Done | `RequiresRefund` stops payout from firing |
| Audit log | ✅ Done | Immutable DB trigger — no UPDATE/DELETE allowed |
| Deal reference sequence | ✅ Done | Format: `SX-2026-000001` |
| Dispute resolution endpoint | ❌ Not done | Admin endpoint to settle disputes and trigger refund or payout |

---

## 4. Fee Calculation ✅

| Service | Formula | Status |
|---|---|---|
| Standard | `max(value × 2.5%, R150)` | ✅ Done |
| Verified Express | `max(value × 1.5%, R150) + R250` | ✅ Done |
| Fee split | Buyer / Seller / 50-50 | ✅ Done |
| Matches frontend JS | ✅ Verified | Tested end to end |

---

## 5. Ozow Payout (Seller) ✅

| Feature | Status | Notes |
|---|---|---|
| `OzowPayoutService` | ✅ Done | Full implementation |
| AES-256-CBC account encryption | ✅ Done | IV = first 16 bytes of SHA512 |
| SHA-512 hash (correct field order) | ✅ Done | Matches Ozow docs exactly |
| `POST /requestpayout` API call | ✅ Done | Correct headers: `SiteCode` + `ApiKey` |
| Auto-fires on buyer accept | ✅ Done | Triggered in `CompleteAsync` |
| Graceful skip if no bank details | ✅ Done | Logs warning, does not crash |
| Staging URL configured | ✅ Done | `https://stagingpayoutsapi.ozow.com/v1` |
| `OZOW_PAYOUT_API_KEY` | ❌ Missing | Get from staging dashboard → Merchant Details |
| `OZOW_SITE_CODE` | ⚠️ Unconfirmed | Current value looks like a placeholder — confirm from dashboard |
| Switch to production URL | ❌ Not done | Change `OZOW_PAYOUT_BASE_URL` env var when going live |

---

## 6. Refund Flow ⚠️

| Feature | Status | Notes |
|---|---|---|
| Funds held in float on dispute | ✅ Done | `RequiresRefund` status prevents payout |
| Dispute resolution endpoint | ❌ Not done | Needs admin endpoint to settle and trigger refund |
| Actual refund to buyer | ❌ Not done | Blocked on Karabo answering: does Ozow support refund from float back to buyer's original payment method, or does SecureX manually EFT? |

---

## 7. KYC ⚠️

| Feature | Status | Notes |
|---|---|---|
| SmileID result codes mapped | ✅ Done | Approved / provisional / retryable / rejected |
| State transitions wired to KYC outcomes | ✅ Done | Buyer KYC → PaymentPending or BuyerKycFailed |
| Actual SmileID/ThisIsMe API call | ❌ Not done | Blocked on ThisIsMe docs + API key from Karabo |
| KYC session initiation | ❌ Not done | `start-buyer-kyc` advances state but doesn't call SmileID yet |

---

## 8. Frontend Integration ✅

| Feature | Status | Notes |
|---|---|---|
| Signup form → `POST /api/transactions` | ✅ Done | Rewired from Formspree |
| Auto-create buyer + seller from form | ✅ Done | No registration needed |
| Success screen with deal reference | ✅ Done | Shows `SX-2026-000001` format |
| Fee calculator on index page | ✅ Done | Matches backend formula |
| CORS configured | ✅ Done | Allows frontend origins |
| JWT auth on `/api/*` endpoints | ✅ Done | Dev token endpoint — temporary |
| Seller bank details collection | ❌ Not done | No UI or endpoint for sellers to submit bank account, branch code, bank group ID |
| Replace dev auth token | ❌ Not done | Confirm auth strategy first — no user login per Karabo |

---

## 9. Reconciliation ⚠️

| Feature | Status | Notes |
|---|---|---|
| Background service | ✅ Done | Runs at 02:00 SAST daily |
| Expected float calculation | ✅ Done | Sums all `FundsSecured` transactions |
| Ozow float fetch | ❌ Stubbed | Returns `0` — blocked on Ozow providing the float API endpoint |
| Alert on discrepancy | ✅ Done | Logs alert when mismatch detected |

---

## 10. Communications ❌

| Feature | Status | Notes |
|---|---|---|
| Email/WhatsApp on deal created | ❌ Not done | Provider not decided |
| Email/WhatsApp on payment received | ❌ Not done | Provider not decided |
| Email/WhatsApp on item delivered | ❌ Not done | Provider not decided |
| Email/WhatsApp on deal completed | ❌ Not done | Provider not decided |
| Email/WhatsApp on dispute raised | ❌ Not done | Provider not decided |
| Low float alert email | ❌ Not done | Ozow sends this automatically to configured email |

---

## Blocked Items Summary

### Waiting on Ozow Staging Dashboard 🔑
| Item | Where to find it |
|---|---|
| `OZOW_PAYOUT_API_KEY` | Staging dashboard → Merchant Details |
| `OZOW_SITE_CODE` (confirm) | Staging dashboard → Merchant Details |

### Waiting on Karabo's Answers ❓
| Question | Why it matters |
|---|---|
| Who gets paid on completion — seller's personal bank account or SecureX business account? | Determines how `TriggerPayoutAsync` routes the money |
| Dispute resolution — who decides the outcome and how does the buyer get refunded from the float? | Needed to build the dispute resolution endpoint |
| ThisIsMe/SmileID docs + API key | Needed to implement real KYC session initiation |
| Comms provider decision (email/WhatsApp) | Needed to build state transition notifications |

### Waiting on Ozow ⏳
| Item | Notes |
|---|---|
| Float API endpoint | Needed for reconciliation service — Teyla to provide |

---

## Environment Variables

| Variable | Status | Notes |
|---|---|---|
| `DATABASE_URL` | ✅ Set | Supabase PostgreSQL |
| `DATABASE_SSL` | ✅ Set | `false` for Supabase pooler |
| `OZOW_ACCESS_TOKEN` | ✅ Set | Webhook security token |
| `OZOW_API_KEY` | ✅ Set | Used for webhook hash verification |
| `OZOW_SITE_CODE` | ⚠️ Unconfirmed | Confirm from staging dashboard |
| `OZOW_ACCOUNT_NUMBER_DECRYPTION_KEY` | ✅ Set | AES key returned to Ozow on verify |
| `OZOW_PAYOUT_API_KEY` | ❌ Missing | Get from staging dashboard |
| `OZOW_PAYOUT_BASE_URL` | ✅ Set | Staging URL — change for production |
| `OZOW_NOTIFY_URL` | ✅ Set | Points to Render notification webhook |
| `JWT_SECRET` | ✅ Set | Dev only — replace before go-live |

---

## API Endpoints

### Public (no auth)
| Method | Endpoint | Status |
|---|---|---|
| GET | `/health` | ✅ Live |
| POST | `/api/auth/token` | ✅ Dev only — remove before go-live |
| POST | `/securex/payout-verify` | ✅ Live |
| POST | `/securex/payout-notification` | ✅ Live |

### Transactions (JWT required)
| Method | Endpoint | Status |
|---|---|---|
| POST | `/api/transactions` | ✅ Live |
| GET | `/api/transactions/{id}` | ✅ Live |
| GET | `/api/transactions/ref/{dealReference}` | ✅ Live |
| GET | `/api/transactions/fee-preview` | ✅ Live |
| POST | `/api/transactions/{id}/start-buyer-kyc` | ✅ Live |
| POST | `/api/transactions/kyc-webhook` | ✅ Live |
| POST | `/api/transactions/{id}/mark-delivered` | ✅ Live |
| POST | `/api/transactions/{id}/accept` | ✅ Live |
| POST | `/api/transactions/{id}/reject` | ✅ Live |
| GET | `/api/transactions/{id}/audit` | ✅ Live |
| POST | `/api/transactions/{id}/resolve-dispute` | ❌ Not built yet |

---

## Go-Live Checklist

- [ ] Get `OZOW_PAYOUT_API_KEY` from staging dashboard
- [ ] Confirm `OZOW_SITE_CODE` from staging dashboard
- [ ] Complete Ozow staging test cases (required for production access)
- [ ] Answer: seller bank account vs SecureX business account
- [ ] Answer: dispute resolution and refund process
- [ ] Build dispute resolution endpoint
- [ ] Build seller bank details collection
- [ ] Wire SmileID/ThisIsMe KYC (blocked on credentials)
- [ ] Decide and wire comms provider
- [ ] Deploy C# API to Render
- [ ] Replace `JWT_SECRET` dev value with strong random secret
- [ ] Remove dev `POST /api/auth/token` endpoint
- [ ] Switch `OZOW_PAYOUT_BASE_URL` to production
- [ ] Complete Ozow production test cases
