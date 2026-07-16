# SecureX — Outstanding Work
Last updated: 8 July 2026

## Unblocked (do now)

- [ ] Deploy C# API to Render — replace old Node.js service
- [ ] Seller bank details — UI + API endpoint to collect `BankAccountNumber`, `BankBranchCode`, `BankGroupId`
- [ ] Dispute resolution endpoint — resolve disputes, trigger refund or payout

## Blocked on Ozow

- [ ] `OZOW_PAYOUT_API_KEY` — email Ozow support to provision
- [ ] Complete Ozow staging tests — once payout API key received
- [ ] Switch `OZOW_PAYOUT_BASE_URL` to production when certified

## Blocked on Karabo

- [ ] Who decides dispute outcomes and how
- [ ] Payout routing rules
- [ ] Refund flow design

## Blocked on Third Parties

- [ ] SmileID/ThisIsMe KYC — need credentials + integration
- [ ] Communications provider (Email/WhatsApp) — no provider chosen yet

## Pre Go-Live

- [ ] Replace `JWT_SECRET` with strong production secret
- [ ] Remove dev `POST /api/auth/token` endpoint
- [ ] Switch `OZOW_PAYOUT_BASE_URL` from staging to production
- [ ] Complete Ozow production certification
