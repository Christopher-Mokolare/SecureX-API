# Teyla Meeting — What We Need & Why

---

## 1. Staging Dashboard Access

**What to ask:** "We can't log into stagingdash.ozow.com with info@secureexchange.co.za — can you send us the credentials or reset the password?"

**Why we need it:**
The staging dashboard is a separate system from the production dashboard (dash.ozow.com). All our test keys, site codes, and payout API keys for staging live there. Without logging in we're flying blind — we can't see our test transactions, check our float balance, or find the payout API key.

---

## 2. Staging Payout API Key

**What to ask:** "Where do we find the Payout API Key on the staging dashboard? Is it the same as the collection API key or a separate key?"

**Why we need it:**
There are two separate Ozow integrations:
- **Collection** — buyer pays money into our escrow float (uses one API key)
- **Payouts** — we send money from the float to the seller's bank account (uses a different API key)

Right now we're using the collection API key for payouts and getting a 403 Forbidden error. The payout API key is what authorises us to move money out of the float to sellers. Without it, the entire seller payout flow is broken — money goes in but can never come out.

---

## 3. Collection Hash — PrivateKey or ApiKey?

**What to ask:** "For the collection hash (pay.ozow.com), does the hash use the PrivateKey or the ApiKey at the end of the input string? And is the IsTest field included in the hash input?"

**Why we need it:**
When a buyer goes to pay, we generate a security hash and send it to Ozow with the payment details. Ozow recalculates the hash on their side and compares — if they don't match, Ozow rejects the payment with "HashCheck value has failed", which is exactly what we're seeing right now.

The hash is built by concatenating all the payment fields in a specific order and hashing them with SHA-512. The last field in that string is either the PrivateKey or the ApiKey — we need to confirm which one. Getting this wrong by one character means no buyer can ever pay.

---

## 4. HTTP vs HTTPS for Webhook URLs

**What to ask:** "On staging, will Ozow accept HTTP notify URLs for collection payments and payouts? Or is HTTPS strictly required?"

**Why we need it:**
When a buyer completes a payment, Ozow sends a notification (webhook) to our server to tell us the payment went through. Our server is currently only on HTTP (no SSL certificate yet). 

If Ozow requires HTTPS even on staging, we can't test the full payment flow until we sort out our SSL certificate. If HTTP is fine on staging, we can test everything now and sort SSL before going to production.

---

## 5. Production Payout API Key

**What to ask:** "Is the production payout API key already generated for our account, or do we need to request it separately? Where do we find it?"

**Why we need it:**
When we go live, real sellers need to receive real money. The production payout API key is what authorises real bank transfers out of our Ozow float to sellers. Without it, we can take payments from buyers but can never pay sellers — which means we'd be holding people's money with no way to release it. We need to know if this key exists already or if there's an approval/onboarding process to get it.
