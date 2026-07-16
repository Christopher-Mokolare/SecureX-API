# SecureX — Complete Project Scope

## What It Is
A South African peer-to-peer digital escrow platform. A buyer and seller agree on a deal, the buyer pays into a secure holding account, the seller only gets paid once the buyer confirms they received the item. SecureX sits in the middle and manages the whole process.

---

## Tech Stack

| Layer | Technology |
|---|---|
| Frontend | HTML/CSS/JS (already live at secureexchange.co.za) |
| Backend | C# ASP.NET Core 8 |
| Database | PostgreSQL (Supabase) |
| Payments | Ozow (inbound collection + outbound payout) |
| KYC | SmileID (identity + liveness) |
| Bank Verification | ThisIsMe |
| Auth | AWS Cognito |
| Hosting | AWS (af-south-1 Cape Town) |
| Comms | WhatsApp + SMS + Email |

---

## What's Built

- Ozow webhook endpoints (verify + notification) with hash validation and idempotency
- 9-step escrow state machine with fee calculations
- Row locking and optimistic concurrency (prevents race conditions)
- Immutable audit log on every state change
- Deal reference generator (SX-2026-000001)
- Daily reconciliation job at 02:00 SAST
- All 9 database tables live on Supabase
- Access token security on webhook endpoints

---

## What Still Needs To Be Built

### 1. Payments
- Ozow payout API call when a deal completes (currently a stub)
- Ozow float balance API for daily reconciliation (currently returns 0)

### 2. KYC & Verification
- SmileID webhook — parse real pass/fail payload
- ThisIsMe bank verification webhook — not started
- Link KYC results to the correct transaction automatically

### 3. Transaction API (Frontend-facing)
- Create user (buyer/seller registration)
- Submit deal form (item, description, price, service type, fee split)
- Get deal status
- Buyer accepts item → triggers payout
- Buyer rejects item → triggers dispute

### 4. Dispute System
- 24-hour inspection window timer after delivery confirmed
- Freeze funds when dispute is raised
- 72-hour adjudication workflow
- Admin dashboard to review and resolve disputes

### 5. Seller Dispatch
- Photo upload endpoint (timestamped dispatch evidence)
- Metadata validation (unaltered EXIF data required)
- Link photos to the transaction

### 6. Auth
- AWS Cognito user pool setup
- JWT verification on all transaction endpoints
- Role-based access (Buyer, Seller, Admin)

### 7. Automated Communications
- WhatsApp/SMS/Email triggers on every state change:
  - Deal created → notify both parties
  - Funds secured → notify seller to dispatch
  - Seller KYC approved → dispatch instructions
  - Item delivered → notify buyer to inspect
  - Deal completed → notify both parties
  - Dispute raised → notify both parties + admin

### 8. Fee Logic Clarification
- Confirm correct fee structure (T&Cs contradict the homepage)
- Implement fee split (buyer pays / seller pays / 50/50)
- Deduct fee from seller payout when seller pays

### 9. Infrastructure
- Terraform for all AWS services (API Gateway, Lambda/EC2, WAF, KMS, Secrets Manager, SQS, S3, CloudWatch, SNS, EventBridge)
- Move all secrets from `.env` into AWS Secrets Manager
- WAF rules (SQL injection, rate limiting, bot blocking)
- CloudWatch alarms
- SSL + domain pointing to deployed API

### 10. Security Hardening
- KMS encryption for SA ID numbers and bank details before DB storage
- S3 bucket for KYC document storage
- Row-level security on DB (buyers can't query other users' transactions)
- DB roles with least privilege (securex_api, securex_audit, securex_reconciliation, securex_admin)

---

## Blockers — Waiting on Karabo

| # | What's Needed | Why |
|---|---|---|
| 1 | Ozow payout API docs + real API key | Can't send money to sellers |
| 2 | Ozow float/reconciliation API | Reconciliation compares against 0 |
| 3 | SmileID webhook payload example | KYC pass/fail parsing is a guess |
| 4 | ThisIsMe webhook payload + API key | Bank verification not started |
| 5 | Correct fee structure confirmation | T&Cs and homepage contradict each other |
| 6 | Cognito preference or alternative auth | Transaction endpoints unprotected |
| 7 | Comms provider (WhatsApp/SMS/Email) | No notifications firing on state changes |
| 8 | New Supabase DB credentials | Current DB is Karabo's personal test instance |

---

## Deployment Checklist (When Ready)

- [ ] All stubs replaced with real API calls
- [ ] Secrets moved to AWS Secrets Manager
- [ ] Terraform infrastructure deployed to af-south-1
- [ ] Domain pointed to deployed API
- [ ] Full 9-step flow tested on Ozow staging
- [ ] KYC flow tested end to end with SmileID + ThisIsMe
- [ ] Dispute flow tested
- [ ] Security review completed
- [ ] POPIA compliance confirmed

---

## Estimated Remaining Work

| Phase | Effort |
|---|---|
| After Karabo provides all docs/keys | ~1 week |
| Dispute system + photo upload | ~3 days |
| Auth (Cognito) + comms | ~3 days |
| Infrastructure (Terraform + AWS) | ~2 days |
| Testing + security hardening | ~2 days |
| **Total** | **~3 weeks** |
