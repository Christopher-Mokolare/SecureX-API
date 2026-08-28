# SecureX Workflow

SecureX is an escrow platform connecting a buyer and seller. It verifies the
buyer, collects payment through Ozow, holds the transaction until delivery is
accepted, and then pays the seller through Ozow Payouts.

## Participants

- **Buyer:** pays for the item and accepts or rejects it after delivery.
- **Seller:** supplies the item, delivery details, and bank details.
- **SecureX:** calculates fees, manages the escrow state, and coordinates the
  payment and payout.
- **SmileID:** performs buyer Enhanced KYC and AML screening.
- **Ozow:** collects the buyer payment and sends the seller payout.

## Transaction Creation

The buyer submits the item, price, buyer details, seller details, and consent.
The API creates a transaction with status `PaymentPending`.

During creation, SecureX starts two independent SmileID checks:

1. Enhanced KYC verifies the buyer identity.
2. AML Check screens the buyer against relevant lists.

The transaction cannot receive a payment link until both checks are
`Approved`.

KYC results arrive asynchronously through the SmileID webhook. AML results are
stored independently from KYC results. A KYC approval does not imply AML
approval.

## Fees

The current pricing configuration is:

- **Standard Escrow:** 2.5% of the item value, minimum R150.
- **Verified Express:** 1.5% of the item value, minimum R150, plus R250.

The platform fee is split according to `FeePayer`:

| Fee payer | Buyer checkout | Seller payout |
|---|---:|---:|
| Buyer | Item value + full fee | Item value |
| Seller | Item value | Item value - full fee |
| Split | Item value + half fee | Item value - half fee |

The amount sent to Ozow for collection is:

```text
ItemValue + BuyerFee
```

The amount requested from Ozow for seller payout is:

```text
ItemValue - SellerFee
```

Provider fees, refunds, fraud losses, support, and operating costs must be
deducted when calculating actual profit.

## Buyer Payment

Once the buyer's KYC and AML statuses are approved, the frontend requests a
payment link from:

```text
POST /api/transactions/{id}/payment-link
```

SecureX sends the checkout amount to Ozow Collection. Ozow returns a hosted
payment URL. The buyer completes payment on Ozow's staging or production
payment page.

Ozow sends the payment result to:

```text
POST /securex/payment-notification
```

After a verified `Complete` notification, SecureX moves the transaction from
`PaymentPending` to `FundsSecured`.

## Delivery and Release

After funds are secured:

1. The seller submits bank details.
2. The seller starts logistics.
3. The seller marks the item delivered.
4. A 24-hour inspection window starts.
5. The buyer accepts the item.
6. SecureX moves the transaction to `Completed`.
7. SecureX submits an Ozow Payout to the seller.

The payout is submitted to:

```text
POST https://stagingpayoutsapi.ozow.com/v1/requestpayout
```

The production endpoint is selected through configuration. SecureX stores the
Ozow payout ID and tracks its status through callbacks and polling.

## Refund and Dispute Flow

If the buyer rejects the item during the inspection window, the transaction
moves to `RequiresRefund`. An administrator can then:

- release the funds to the seller, which triggers a payout; or
- mark the transaction as refunded after processing the refund through Ozow.

## Ozow Payout Callbacks

Ozow sends a pre-payout verification request to:

```text
POST /securex/payout-verify
```

SecureX validates the access token and hash, then returns the account-number
decryption key required by Ozow.

Ozow sends payout status notifications to:

```text
POST /securex/payout-notification
```

Terminal payout statuses are recorded as complete, failed, returned, or
cancelled. Notifications are idempotent, and the background payout poller
checks payouts when a callback is delayed.

## Main API Endpoints

| Method | Endpoint | Purpose |
|---|---|---|
| POST | `/api/transactions` | Create a transaction and start KYC/AML |
| GET | `/api/transactions/{id}` | Read transaction status |
| GET | `/api/transactions/ref/{reference}` | Read transaction by deal reference |
| POST | `/api/transactions/{id}/payment-link` | Create the Ozow collection link |
| POST | `/api/users/{id}/bank-details` | Save seller payout details |
| POST | `/api/transactions/{id}/start-logistics` | Start delivery |
| POST | `/api/transactions/{id}/mark-delivered` | Mark item delivered |
| POST | `/api/transactions/{id}/accept` | Accept item and trigger payout |
| POST | `/api/transactions/{id}/reject` | Reject item and start dispute flow |

## Environments

The current hosted setup uses:

- Frontend: Firebase Hosting
- API: Render
- Database: Neon PostgreSQL
- SmileID: Sandbox during testing
- Ozow: Staging during testing

Production requires production SmileID and Ozow credentials, production
endpoints, verified callback URLs, rotated secrets, and a confirmed compliance
and escrow operating model.

## Testing

Run the automated SecureX smoke test:

```bash
./test-smileid-e2e.sh
```

This verifies API health, authentication, transaction creation, KYC, AML,
status correlation, and Ozow payment-link creation.

Ozow payout certification tests are run with:

```bash
bash test-ozow-cases.sh mock
bash test-ozow-cases.sh live
```

The mock cases are automated through Ozow's mock API. Verification requests,
payout completion, cancellation, low-float alerts, and dashboard evidence
require Ozow Staging to process or simulate the payout and send callbacks.
