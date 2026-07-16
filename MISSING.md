# SecureX — Outstanding Items
Last updated: 8 July 2026

This document lists everything that is still missing or incomplete before SecureX can go live.
Items are grouped by who is blocking them.

**Legend:** 🖥 Frontend &nbsp;|&nbsp; ⚙️ Backend &nbsp;|&nbsp; 🖥⚙️ Both

---

## 1. Unblocked — Can Be Built Now

### 1.1 Seller Bank Details Collection — 🖥⚙️ Both
**What's missing:**
There is no way for a seller to submit their banking information. The `User` model already has `BankAccountNumber`, `BankBranchCode`, and `BankGroupId` fields in the database, but there is no API endpoint or UI form to collect these values.

**Why it matters:**
When a buyer accepts an item and the deal is marked `Completed`, the system automatically triggers a payout to the seller via Ozow. If the seller has no bank details on record, the payout is silently skipped with a warning log. No money reaches the seller.

**What needs to be built:**
- ⚙️ `POST /api/users/{id}/bank-details` — endpoint for sellers to submit their bank account number, branch code, and Ozow bank group ID
- ⚙️ `GET /getavailablebanks` proxy or cached list so the frontend can show a bank picker
- 🖥 A frontend form or screen where the seller fills in their banking details after the deal is created
- 🖥 Bank dropdown populated from the Ozow banks list so the seller can select their bank and the correct `BankGroupId` is submitted

---

### 1.2 Dispute Resolution Endpoint — ⚙️ Backend
**What's missing:**
The endpoint `POST /api/transactions/{id}/resolve-dispute` does not exist yet.

**Why it matters:**
When a buyer rejects an item within the 24-hour inspection window, the transaction moves to `RequiresRefund` status. At that point, funds are held in the Ozow float and neither the buyer nor the seller receives anything. There is currently no way to resolve this — no admin can decide the outcome, and no refund or payout can be triggered.

**What needs to be built:**
- ⚙️ `POST /api/transactions/{id}/resolve-dispute` — admin endpoint accepting `{ decision: "release-to-seller" | "refund-to-buyer" }`
- ⚙️ If `release-to-seller`: call `OzowPayoutService.RequestPayoutAsync` with seller bank details
- ⚙️ If `refund-to-buyer`: trigger refund mechanism (depends on decision in Section 3.1)

---

### 1.3 Deploy C# API to Render — ⚙️ Backend
**What's missing:**
The live URL `https://securex-btit.onrender.com` is currently running the old Node.js webhook service, not the new C# ASP.NET Core 8 API.

**Why it matters:**
All the new functionality — deal creation, state machine, KYC, payout triggering, audit logs — lives in the C# API. The frontend and Ozow webhooks are pointing at the Render URL, but the C# API has never been deployed there.

**What needs to be done:**
- ⚙️ Add a `Dockerfile` or configure Render to build and run the C# project from `SecureX.Api/`
- ⚙️ Set all environment variables on Render (database URL, Ozow keys, JWT secret, etc.)
- ⚙️ Verify the health endpoint `GET /health` returns 200 after deploy
- ⚙️ Decommission the old Node.js service once confirmed working

---

## 2. Blocked on Ozow

### 2.1 Payout API Activation — ⚙️ Backend
**What's missing:**
The Ozow Payouts API (`stagingpayoutsapi.ozow.com`) is returning `403 Missing Authentication Token` for all requests, including `GET /getavailablebanks`. This means the merchant account `SECUREXPTYLTD` (site code `SEC-SEC-014`) has not been activated for payout access on the staging environment.

**What needs to happen:**
- ⚙️ Ozow support must manually activate payout access for the account and provide a Payout API Key — requested via email to support@ozow.com

**Impact:**
Until this is resolved, no staging payout tests can be run and the end-to-end payout flow cannot be verified.

---

### 2.2 Ozow Staging Test Cases — ⚙️ Backend
**What's missing:**
Ozow requires merchants to complete a set of mandatory staging test scenarios before granting production access. These tests cover:
- Successful payout flow
- Verification webhook returning `isVerified: false`
- Account number decryption failure
- Payout cancellation

**What needs to happen:**
- ⚙️ Once payout API access is activated (2.1 above), run and pass all required Ozow staging test scenarios

---

### 2.3 Ozow Float API for Reconciliation — ⚙️ Backend
**What's missing:**
The daily reconciliation service runs at 02:00 SAST and compares the expected float (sum of all `FundsSecured` transactions) against the actual Ozow float balance. The Ozow float fetch is currently stubbed and always returns `0`.

**What needs to happen:**
- ⚙️ Ozow (Teyla) to provide the float balance API endpoint
- ⚙️ Replace the stubbed `return 0` in `ReconciliationService` with a real API call

---

## 3. Blocked on Business Decisions (Karabo)

### 3.1 Refund Flow — How Does the Buyer Get Their Money Back? — 🖥⚙️ Both
**What's missing:**
When a dispute is resolved in the buyer's favour, the funds are sitting in the Ozow float. There is no implemented mechanism to return those funds to the buyer.

**Decision needed from Karabo:**
- Does Ozow support a direct refund from the float back to the buyer's original payment method? If yes, what is the API call?
- Or does SecureX manually EFT the buyer from the business account?

**Once decided:**
- ⚙️ Implement the refund call in the dispute resolution endpoint
- 🖥 Show the buyer a refund confirmation screen with expected timeline

---

### 3.2 Payout Routing — Seller Account or SecureX Business Account? — ⚙️ Backend
**What's missing:**
When a deal completes, the payout currently goes directly to the seller's personal bank account. It is unclear whether this is the intended flow or whether funds should first land in the SecureX business account before being disbursed to the seller.

**Decision needed from Karabo:**
- Does the payout go directly to the seller's bank account via Ozow?
- Or does it go to the SecureX Capitec Business account (`1055374116`) first?

**Once decided:**
- ⚙️ Update `TriggerPayoutAsync` to use the correct destination bank details

---

### 3.3 KYC Provider — ThisIsMe / SmileID — 🖥⚙️ Both
**What's missing:**
The KYC state machine is fully wired up. When `POST /api/transactions/{id}/start-buyer-kyc` is called, the transaction moves to `BuyerKycPending`. However, no actual KYC session is initiated — the system never calls SmileID or ThisIsMe.

**What needs to happen:**
- ⚙️ Karabo to provide ThisIsMe or SmileID API credentials and documentation
- ⚙️ Update `POST /api/transactions/{id}/start-buyer-kyc` to call the KYC provider and return a session URL or SDK token
- ⚙️ Update `POST /api/transactions/kyc-webhook` to handle the real provider's callback format
- 🖥 Frontend to redirect the user to the KYC session URL or embed the SDK

---

### 3.4 Communications Provider — Email and WhatsApp Notifications — ⚙️ Backend
**What's missing:**
There are no notifications of any kind. Buyers and sellers receive no emails or WhatsApp messages at any point in the deal lifecycle.

**Notifications needed at minimum:**
| Trigger | Recipient | Message |
|---|---|---|
| Deal created | Buyer + Seller | Deal reference, item details, next steps |
| Payment received | Seller | Funds secured, item to be shipped |
| Item marked delivered | Buyer | 24-hour inspection window started |
| Deal completed | Seller | Payout initiated |
| Dispute raised | Both | Dispute under review |
| Dispute resolved | Both | Outcome and next steps |

**Decision needed from Karabo:** Which provider to use — options include SendGrid (email), Twilio (WhatsApp/SMS), or a combined platform like Bird or Vonage.

**Once decided:**
- ⚙️ Wire provider SDK into the API
- ⚙️ Send notifications on each state transition: deal created, payment received, item delivered, completed, dispute raised, dispute resolved

---

## 4. Pre Go-Live (Do Last)

These items must be completed before switching to production but should not be done until everything above is resolved.

| Item | Layer | Detail |
|---|---|---|
| Remove dev auth endpoint | ⚙️ Backend | `POST /api/auth/token` issues JWTs with no user verification — must be removed before go-live |
| Replace JWT secret | ⚙️ Backend | Current `JWT_SECRET` is a weak dev placeholder — replace with a cryptographically random 64-character secret |
| Switch payout URL to production | ⚙️ Backend | Change `OZOW_PAYOUT_BASE_URL` from `https://stagingpayoutsapi.ozow.com/v1` to `https://payoutsapi.ozow.com/v1` |
| Complete Ozow production certification | ⚙️ Backend | Ozow requires a separate set of production test cases to be passed before live payouts are enabled |

---

## Summary

| # | Item | Layer | Blocked By |
|---|---|---|---|
| 1.1 | Seller bank details UI + endpoint | 🖥⚙️ Both | Nothing — can build now |
| 1.2 | Dispute resolution endpoint | ⚙️ Backend | Nothing (logic) + Karabo (refund method) |
| 1.3 | Deploy C# API to Render | ⚙️ Backend | Nothing — can do now |
| 2.1 | Ozow payout API activation | ⚙️ Backend | Ozow support |
| 2.2 | Ozow staging test cases | ⚙️ Backend | Ozow (2.1 must be done first) |
| 2.3 | Ozow float API for reconciliation | ⚙️ Backend | Ozow (Teyla) |
| 3.1 | Refund flow design | 🖥⚙️ Both | Karabo |
| 3.2 | Payout routing decision | ⚙️ Backend | Karabo |
| 3.3 | KYC integration | 🖥⚙️ Both | Karabo (credentials) |
| 3.4 | Communications provider | ⚙️ Backend | Karabo (provider decision) |
| 4.x | Pre go-live cleanup | ⚙️ Backend | Do last |
