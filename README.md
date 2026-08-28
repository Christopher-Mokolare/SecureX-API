# SecureX API

Escrow platform backend — ASP.NET Core 8, PostgreSQL (RDS), Ozow payments.

See [SECUREX_WORKFLOW.md](SECUREX_WORKFLOW.md) for the complete product and
technical transaction workflow.

## Stack

| Layer | Technology |
|---|---|
| API | ASP.NET Core 8 |
| Database | PostgreSQL 16 on AWS RDS (af-south-1) |
| Payments | Ozow (inbound collection + outbound payout) |
| KYC / AML | SmileID Enhanced KYC + AML Check |
| Auth | JWT |
| Hosting | AWS ECS Fargate (af-south-1) |
| Load Balancer | AWS ALB — `securex-alb-1751040376.af-south-1.elb.amazonaws.com` |

## Endpoints

### Public
| Method | Endpoint | Description |
|---|---|---|
| GET | `/health` | Health check |
| POST | `/securex/payout-verify` | Ozow pre-payout verification webhook |
| POST | `/securex/payout-notification` | Ozow payout outcome webhook |
| POST | `/api/transactions/kyc-webhook` | SmileID KYC result callback |

### Transactions (JWT required)
| Method | Endpoint | Description |
|---|---|---|
| POST | `/api/transactions` | Create deal |
| GET | `/api/transactions/{id}` | Get deal by ID |
| GET | `/api/transactions/ref/{dealReference}` | Get deal by reference |
| GET | `/api/transactions/fee-preview` | Preview fee calculation |
| POST | `/api/transactions/{id}/mark-delivered` | Seller marks item delivered |
| POST | `/api/transactions/{id}/accept` | Buyer accepts item → triggers payout |
| POST | `/api/transactions/{id}/reject` | Buyer rejects item → raises dispute |
| POST | `/api/transactions/{id}/resolve-dispute` | Admin resolves dispute |
| GET | `/api/transactions/{id}/audit` | Full audit log |

### Users (JWT required)
| Method | Endpoint | Description |
|---|---|---|
| GET | `/api/users/{id}` | Get user |
| POST | `/api/users/{id}/bank-details` | Save seller bank details |
| GET | `/api/users/banks` | List available banks (Ozow proxy) |

## Environment Variables

| Variable | Purpose |
|---|---|
| `DATABASE_URL` | PostgreSQL connection string |
| `DATABASE_SSL` | Set `true` for RDS |
| `JWT_SECRET` | JWT signing secret |
| `OZOW_ACCESS_TOKEN` | Webhook security token |
| `OZOW_API_KEY` | Ozow API key |
| `OZOW_SITE_CODE` | Ozow site code |
| `OZOW_PRIVATE_KEY` | Ozow private key |
| `OZOW_PAYOUT_API_KEY` | Ozow payout API key |
| `OZOW_PAYOUT_BASE_URL` | Staging or production payout base URL |
| `OZOW_VERIFY_URL` | Ozow payout verification callback URL |
| `OZOW_ACCOUNT_NUMBER_DECRYPTION_KEY` | AES key returned on payout verify |
| `SMILEID_PARTNER_ID` | SmileID partner ID |
| `SMILEID_API_KEY` | SmileID API key |
| `SMILEID_BASE_URL` | SmileID base URL |
| `SMILEID_CALLBACK_URL` | HTTPS SmileID webhook callback URL |
| `SMILEID_POLICY_URL` | HTTPS privacy policy URL supplied with consent |
| `THISISME_API_KEY` | ThisIsMe AVS API key |
| `THISISME_BASE_URL` | ThisIsMe base URL |

## Deploy

Push to `main` — GitHub Actions builds the Docker image, pushes to ECR, and deploys to ECS automatically.

```bash
git push origin main
```

## Local Development

```bash
cd SecureX.Api
dotnet run
```

API runs on `http://localhost:8080`. Swagger UI available at `http://localhost:8080/swagger` in development.
