#!/usr/bin/env bash
cd "$(dirname "$0")/SecureX.Api"

export JWT_SECRET="securex-dev-secret-change-in-production-min32chars!"
export DATABASE_URL="Host=ep-frosty-mouse-b1dgl52g-pooler.c-5.eu-central-1.aws.neon.tech;Port=5432;Database=neondb;Username=neondb_owner;Password=npg_hrXI3dJZycF2;SSL Mode=Require;Trust Server Certificate=true"
export ASPNETCORE_ENVIRONMENT=Staging
export OZOW_ACCESS_TOKEN=3LSEBxE5dG1La5ei1acb4jYm
export OZOW_PAYOUT_API_KEY=mMPd7XC1zJjDSAh5Wcpf9I6XBl
export OZOW_SITE_CODE=SEC-SEC-004
export OZOW_ACCOUNT_NUMBER_DECRYPTION_KEY=3LSEBxE5dG1La5ei1acb4jYm
export OZOW_PAYOUT_BASE_URL=https://stagingpayoutsapi.ozow.com/v1
export OZOW_NOTIFY_URL=https://securex-api-vjf3.onrender.com/securex/payout-notification
export OZOW_VERIFY_URL=${NGROK_URL:-https://PLACEHOLDER.ngrok-free.app}/securex/payout-verify

echo "OZOW_VERIFY_URL=$OZOW_VERIFY_URL"
dotnet run
