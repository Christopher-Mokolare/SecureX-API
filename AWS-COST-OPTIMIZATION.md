# SecureX — AWS Cost Optimization Plan

## Current State

| Service | Monthly Cost (est.) |
|---|---|
| ECS Fargate (prod) | ~$8–12 (R136–204) |
| ECS Fargate (staging) | ~$8–12 (R136–204) |
| ALB (Application Load Balancer) | ~$16–18 (R272–306) |
| RDS PostgreSQL af-south-1 | ~$13–15 (R221–255) |
| Public IPv4 addresses | ~$3.60 (R61) |
| ECR + SSM + CloudWatch logs | ~$1.30 (R22) |
| **Total** | **~$45–55/month (R765–935/month)** |

## Target State

| Service | Monthly Cost (est.) |
|---|---|
| ECS Fargate (prod) | ~$8–12 (R136–204) |
| ECS Fargate (staging) | ~$0 (scaled to 0 when idle) |
| API Gateway HTTP API (replacing ALB) | ~$1–3 (R17–51) |
| RDS PostgreSQL af-south-1 | ~$13–15 (R221–255) |
| Public IPv4 | ~$0–1 (R0–17) |
| ECR + SSM + CloudWatch logs (capped) | ~$1 (R17) |
| **Total** | **~$22–30/month (R374–510/month)** |

**Estimated monthly saving: ~$20–30/month (R340–510/month at R17/USD)**

---

## Why Stay on AWS

### 1. Background Services Cannot Tolerate Cold Starts

SecureX runs three critical background services that must be always-on:

- `OzowPayoutPollerService` — polls every 2 minutes for overdue payouts
- `InspectionWindowExpiryService` — runs every 5 minutes to auto-accept expired deals
- `ReconciliationService` — runs daily at 02:00 SAST to verify float balance

Any platform with a free-tier spin-down (e.g. Render free plan) silently kills these services. Missed payout polls = unresolved transactions. Missed inspection expiry = stuck deals.

### 2. Database Must Stay in a Private VPC

RDS is in `af-south-1` inside a private subnet. It is not internet-facing. Moving compute off AWS means the database would need a public IP or VPN tunnel — both are unacceptable for a platform storing:

- Seller bank account numbers (KMS-encrypted at rest)
- Buyer ID numbers (KMS-encrypted at rest)
- KYC/AML status and SmileID job references
- Transaction amounts and payout records

### 3. Latency for South African Users

RDS is in Cape Town (`af-south-1`). ECS is in the same region. Round-trip DB query latency is 2–5ms. Any alternative compute region (Frankfurt, Oregon) adds 160–300ms per query — unacceptable for a payment platform where users expect instant feedback.

### 4. Webhook Reliability

Ozow and SmileID send webhooks that must be acknowledged quickly. ECS Fargate is always running. There are no cold starts, no spin-down delays, no missed callbacks.

### 5. Cost Difference Is Minimal

The difference between optimized AWS (~$22–30/month / R374–510/month) and the cheapest alternative (Render + Render DB, ~$14/month / R238/month) is ~$8–16/month (R136–272/month). That delta does not justify the security, latency, and reliability tradeoffs for a live payment platform.

---

## Optimization Actions

### Phase 1 —

#### 1a. Scale staging ECS to 0 immediately

```bash
aws ecs update-service \
  --cluster securex-staging \
  --service securex-api-staging \
  --desired-count 0 \
  --region af-south-1
```

> **Do NOT scale production to 0.** Prod runs OzowPayoutPollerService,
> InspectionWindowExpiryService, and ReconciliationService continuously.

#### 1b. Set CloudWatch log retention

```bash
# Production — 30 days
aws logs put-retention-policy \
  --log-group-name /ecs/securex-api \
  --retention-in-days 30 \
  --region af-south-1

# Staging — 7 days
aws logs put-retention-policy \
  --log-group-name /ecs/securex-api-staging \
  --retention-in-days 7 \
  --region af-south-1
```

#### 1c. Register staging for scheduled auto scale-down

```bash
aws application-autoscaling register-scalable-target \
  --service-namespace ecs \
  --resource-id service/securex-staging/securex-api-staging \
  --scalable-dimension ecs:service:DesiredCount \
  --min-capacity 0 --max-capacity 1 \
  --region af-south-1

aws application-autoscaling put-scheduled-action \
  --service-namespace ecs \
  --resource-id service/securex-staging/securex-api-staging \
  --scalable-dimension ecs:service:DesiredCount \
  --scheduled-action-name scale-down-nightly \
  --schedule "cron(0 18 * * ? *)" \
  --scalable-target-action MinCapacity=0,MaxCapacity=0 \
  --region af-south-1
```

This scales staging to 0 every night at 20:00 SAST (18:00 UTC).

**Phase 1 saving: ~$8–12/month (R136–204/month)**

---

### Phase 2 — Replace ALB with API Gateway HTTP API

This is the single biggest saving. The ALB costs ~$16–18/month (R272–306/month) just to exist regardless of traffic. API Gateway HTTP API costs ~$1/million requests — effectively free at SecureX's current scale.

#### Architecture change

```
Before:  Internet → ALB → ECS (private subnet)
After:   Internet → API Gateway HTTP API → VPC Link → ECS (private subnet)
```

The database stays in the same private subnet. Nothing changes for RDS.

#### Steps

**Step 1 — Create a VPC Link**

In the AWS Console → API Gateway → VPC Links → Create:
- Type: HTTP (not REST)
- Name: `securex-vpclink`
- VPC: your existing SecureX VPC
- Subnets: the private subnets where ECS runs
- Security group: the same security group your ALB currently uses to reach ECS

**Step 2 — Create an HTTP API**

In API Gateway → Create API → HTTP API:
- Name: `securex-api-gw`
- Integration: Private resource → VPC Link → `securex-vpclink`
- Target: your ECS service's Cloud Map service discovery DNS, or the internal ALB DNS if you keep an internal ALB

**Step 3 — Add a catch-all route**

Route: `ANY /{proxy+}` → forward to VPC Link integration

**Step 4 — Update DNS**

Point your domain (or update the ALB DNS references in Ozow/SmileID dashboards) to the new API Gateway invoke URL:
```
https://<api-id>.execute-api.af-south-1.amazonaws.com
```

**Step 5 — Update ECS task definition env vars**

Update these three env vars in your ECS task definition to use the new API Gateway URL:
- `OZOW_VERIFY_URL`
- `OZOW_NOTIFY_URL`
- `SMILEID_CALLBACK_URL`

**Step 6 — Test all webhooks**

Before deleting the ALB, run your existing test scripts against the new URL:
```bash
BASE_URL=https://<api-id>.execute-api.af-south-1.amazonaws.com bash test-e2e.sh
```

**Step 7 — Delete the ALB**

Only after all webhooks are confirmed working. In the AWS Console → EC2 → Load Balancers → select the SecureX ALB → Actions → Delete.

**Phase 2 saving: ~$15–18/month (R255–306/month)**

---

### Phase 3 — CI/CD: Auto scale-down staging after deploy

Your `deploy.yml` deploys to staging on every `develop` push and leaves it running. Add a step to scale it back down after the deploy stabilises.

In `.github/workflows/deploy.yml`, add after the `Deploy to ECS` step:

```yaml
- name: Scale down staging after deploy
  if: github.ref_name == 'develop'
  run: |
    sleep 300
    aws ecs update-service \
      --cluster securex-staging \
      --service securex-api-staging \
      --desired-count 0 \
      --region af-south-1
```

This ensures staging never runs overnight after a deploy.

**Phase 3 saving: Prevents regression, no additional saving beyond Phase 1**

---

### Phase 4 — Operational Hardening (Before Production Launch)

These are not cost items but are required before going live with real money.

#### 4a. Wire up SNS alert in ReconciliationService

`ReconciliationService.cs` has a `// TODO: publish to SNS` comment. When a float discrepancy is detected, it currently only logs. This means a real discrepancy could go unnoticed until someone checks CloudWatch.

Create an SNS topic `securex-alerts` and publish to it when `alertFired == true`. See `BACKEND-TODOS.md` for the implementation task.

#### 4b. Confirm production Ozow env vars are explicit

`appsettings.json` defaults to:
```json
"CollectionBaseUrl": "https://stagingapi.ozow.com",
"IsTest": "true"
```

These are overridden by env vars in ECS, but if an env var is ever missing from the task definition, the app silently falls back to staging Ozow. Confirm these are explicitly set in the prod ECS task definition:
- `OZOW_IS_TEST=false`
- `OZOW_COLLECTION_BASE_URL=https://api.ozow.com`

#### 4c. Clean up render.yaml

`render.yaml` is still in the repo with `region: oregon` and `plan: free`. If accidentally deployed, background services will die silently on the free plan. Either delete the file or update it to reflect the correct region and plan so it cannot be accidentally used.

---

## Summary

| Phase | Action | Saving |
|---|---|---|
| 1 | Scale staging to 0 + log retention + scheduled scale-down | ~$8–12/month (R136–204/month) |
| 2 | Replace ALB with API Gateway HTTP API | ~$15–18/month (R255–306/month) |
| 3 | Auto scale-down staging in CI/CD | Prevents regression |
| 4 | SNS alert, Ozow prod env vars, render.yaml cleanup | Financial safety |
| **Total** | | **~$23–30/month saved (R391–510/month saved)** |

## What Not to Do

| Action | Reason |
|---|---|
| Move to Render | DB exposed to internet, 200–300ms latency, background services die on free plan |
| Scale prod ECS to 0 | Kills OzowPayoutPollerService, InspectionWindowExpiryService, ReconciliationService |
| Move RDS out of af-south-1 | Adds 160–300ms latency for all SA users |
| Move RDS to public subnet | Payment data must stay in private VPC |
| Delete render.yaml without updating webhook URLs | Ozow/SmileID callbacks will break if URLs still point to old ALB |
