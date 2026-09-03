# SecureX Backend — TODO List

TODOs required to complete AWS cost optimization and production readiness.
Ordered by priority.

---

## CRITICAL — Financial Safety

### TODO-01: Wire up SNS alert in ReconciliationService

**File:** `SecureX.Api/Services/ReconciliationService.cs`
**Line:** ~57 (`// TODO: publish to SNS`)

When a float discrepancy is detected between the expected float (sum of in-flight transactions) and the Ozow float balance, the service currently only logs a critical message. No external alert fires.

**What to do:**
1. Create an SNS topic `securex-alerts` in `af-south-1`
2. Add `AWSSDK.SimpleNotificationService` NuGet package
3. Inject `IAmazonSimpleNotificationService` into `ReconciliationService`
4. Replace the `// TODO` comment with a `PublishAsync` call when `alertFired == true`
5. Add `SNS_ALERT_TOPIC_ARN` env var to ECS task definition
6. Subscribe your email/phone to the SNS topic in the AWS Console

```csharp
// Replace the TODO comment with:
if (alertFired)
{
    await sns.PublishAsync(new PublishRequest
    {
        TopicArn = config["SNS_ALERT_TOPIC_ARN"],
        Subject  = "SecureX Float Discrepancy",
        Message  = $"Expected R{expectedFloat}, Ozow R{ozowFloat}, diff R{discrepancy}"
    });
}
```

---

## HIGH — Production Correctness

### TODO-02: Confirm production Ozow env vars are explicit in ECS task definition

**File:** `SecureX.Api/appsettings.json`

`appsettings.json` defaults to staging Ozow values:
```json
"CollectionBaseUrl": "https://stagingapi.ozow.com",
"IsTest": "true"
```

If `OZOW_IS_TEST` or `OZOW_COLLECTION_BASE_URL` are ever missing from the prod ECS task definition, the app silently processes real payments against Ozow staging.

**What to do:**
- Open the prod ECS task definition in the AWS Console
- Confirm these env vars are explicitly set:
  - `OZOW_IS_TEST=false`
  - `OZOW_COLLECTION_BASE_URL=https://api.ozow.com`
- Do the same for `OZOW_PAYOUT_BASE_URL` — confirm it points to the Ozow production payout URL, not staging

---

### TODO-03: Update or delete render.yaml

**File:** `render.yaml`

Currently configured with `region: oregon` and `plan: free`. If accidentally deployed:
- Background services (`OzowPayoutPollerService`, `InspectionWindowExpiryService`, `ReconciliationService`) will die silently after 15 minutes of inactivity on the free plan
- All DB queries will cross the Atlantic (Oregon → Cape Town, ~250ms per query)

**What to do (pick one):**
- Delete the file entirely if Render is no longer a deployment target
- Or update it to `region: frankfurt` and `plan: starter` as a documented fallback, with a comment warning about background service limitations

---

## MEDIUM — AWS Cost Optimization

### TODO-04: Scale staging ECS to 0

**Action:** Run once via AWS CLI

```bash
aws ecs update-service \
  --cluster securex-staging \
  --service securex-api-staging \
  --desired-count 0 \
  --region af-south-1
```

Saves ~$8–12/month immediately.

---

### TODO-05: Set CloudWatch log retention

**Action:** Run once via AWS CLI

```bash
aws logs put-retention-policy \
  --log-group-name /ecs/securex-api \
  --retention-in-days 30 \
  --region af-south-1

aws logs put-retention-policy \
  --log-group-name /ecs/securex-api-staging \
  --retention-in-days 7 \
  --region af-south-1
```

Without this, logs accumulate indefinitely and cost grows over time.

---

### TODO-06: Register staging for nightly scheduled scale-down

**Action:** Run once via AWS CLI

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

Scales staging to 0 every night at 20:00 SAST (18:00 UTC).

---

### TODO-07: Add staging scale-down step to deploy.yml

**File:** `.github/workflows/deploy.yml`

After the `Deploy to ECS` step, add:

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

Prevents staging from running overnight after a `develop` push.

---

### TODO-08: Replace ALB with API Gateway HTTP API

**Saves:** ~$15–18/month (biggest single saving)

Steps:
1. Create VPC Link in API Gateway → VPC Links (HTTP type, same VPC/subnets as ECS)
2. Create HTTP API in API Gateway with `ANY /{proxy+}` route → VPC Link integration
3. Update DNS to point to the new API Gateway invoke URL
4. Update these ECS task definition env vars to the new URL:
   - `OZOW_VERIFY_URL`
   - `OZOW_NOTIFY_URL`
   - `SMILEID_CALLBACK_URL`
5. Run `test-e2e.sh` against the new URL to confirm all webhooks work
6. Delete the ALB only after all webhooks are confirmed

See `AWS-COST-OPTIMIZATION.md` Phase 2 for full step-by-step.

---

## LOW — Code Hygiene

### TODO-09: Remove hardcoded AWS account ID from ssm-policy.json

**File:** `ssm-policy.json`

The account ID `852009799663` is hardcoded in the resource ARN:
```json
"arn:aws:ssm:af-south-1:852009799663:parameter/securex/*"
```

Not a credential leak, but account IDs in source control are not best practice.

**What to do:**
- Parameterise via a variable or document it as intentional
- Or move `ssm-policy.json` out of the repo and manage it directly in IAM

---

### TODO-10: Swagger only in Development — confirm it is not exposed in Production

**File:** `SecureX.Api/Program.cs`

Swagger is correctly gated:
```csharp
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
```

**What to do:**
- Confirm `ASPNETCORE_ENVIRONMENT=Production` is set in the prod ECS task definition
- Confirm `ASPNETCORE_ENVIRONMENT=Staging` is set in the staging ECS task definition
- Swagger should never be reachable on the production URL

---

### TODO-11: simulate-payment and retry-payout endpoints — confirm disabled or protected in production

**File:** `SecureX.Api/Controllers/TransactionsController.cs`

`POST /api/transactions/{id}/simulate-payment` and `POST /api/transactions/{id}/retry-payout` are protected by `OZOW_ACCESS_TOKEN` header check, not by JWT. This is acceptable for admin use, but confirm:

- `OZOW_ACCESS_TOKEN` is a strong, unique secret in the prod ECS task definition
- These endpoints are documented as internal-only and not exposed in any public API docs

---

## Completion Checklist

| # | TODO | Priority | Done |
|---|---|---|---|
| 01 | SNS alert in ReconciliationService | Critical | [ ] |
| 02 | Confirm prod Ozow env vars in ECS | High | [ ] |
| 03 | Update or delete render.yaml | High | [ ] |
| 04 | Scale staging ECS to 0 | Medium | [ ] |
| 05 | Set CloudWatch log retention | Medium | [ ] |
| 06 | Register staging nightly scale-down | Medium | [ ] |
| 07 | Add scale-down step to deploy.yml | Medium | [ ] |
| 08 | Replace ALB with API Gateway | Medium | [ ] |
| 09 | Remove hardcoded account ID from ssm-policy.json | Low | [ ] |
| 10 | Confirm Swagger not exposed in production | Low | [ ] |
| 11 | Confirm simulate-payment/retry-payout protected | Low | [ ] |
