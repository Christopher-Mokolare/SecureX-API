#!/usr/bin/env node
// Ozow Payout staging test
// Usage: node scripts/test-ozow-payout-staging.js

import crypto from 'crypto';

const SITE_CODE = 'SEC-SEC-014';
const API_KEY   = process.env.OZOW_TEST_KEY ?? 'a2f5cc7f0254ce5002c742ccf79cbd6a';
const BASE_URL  = 'https://stagingpayoutsapi.ozow.com/v1';

// ── 1. Get test configuration ────────────────────────────────────────────────
async function getTestConfig() {
  const url = `${BASE_URL}/gettestconfiguration?siteCode=${SITE_CODE}`;
  const res = await fetch(url, { headers: { ApiKey: API_KEY, Accept: 'application/json' } });
  const body = await res.text();
  console.log(`\nGET /gettestconfiguration → ${res.status}`);
  console.log(body ? JSON.parse(body) : '(empty — no test config set yet)');
  return res.status === 200;
}

// ── 2. Get available banks ───────────────────────────────────────────────────
async function getAvailableBanks() {
  const res = await fetch(`${BASE_URL}/getavailablebanks`, {
    headers: { SiteCode: SITE_CODE, ApiKey: API_KEY, Accept: 'application/json' },
  });
  const raw = await res.text();
  const body = JSON.parse(raw);
  const banks = Array.isArray(body) ? body : [];
  console.log(`\nGET /getavailablebanks → ${res.status}`, Array.isArray(body) ? `(${banks.length} banks)` : body);
  const capitec = banks.find(b => b.bankGroupName?.toLowerCase().includes('capitec'));
  if (capitec) console.log('Capitec entry:', capitec);
  return banks;
}

// ── 3. Request payout ────────────────────────────────────────────────────────
function encryptAccountNumber(accountNumber, amountCents, encryptionKey) {
  const ivInput = `${encryptionKey}${amountCents}${encryptionKey}`;
  const ivHash  = crypto.createHash('sha512').update(ivInput, 'utf8').digest();
  const iv      = ivHash.slice(0, 16);
  const key     = crypto.createHash('sha256').update(encryptionKey, 'utf8').digest();

  const cipher  = crypto.createCipheriv('aes-256-cbc', key, iv);
  const encrypted = Buffer.concat([cipher.update(accountNumber, 'utf8'), cipher.final()]);
  return encrypted.toString('hex');
}

function buildHash(siteCode, amount, merchantRef, customerRef, isRtc, notifyUrl, bankGroupId, accountNumber, branchCode, privateKey) {
  const amountCents = Math.round(amount * 100);
  const raw = `${siteCode}${amountCents}${merchantRef}${customerRef}${String(isRtc).toLowerCase()}${notifyUrl}${bankGroupId}${accountNumber}${branchCode}${privateKey}`;
  return crypto.createHash('sha512').update(raw.toLowerCase(), 'utf8').digest('hex');
}

async function requestPayout(banks) {
  // Use Capitec if found, otherwise first bank
  const bank = banks.find(b => b.bankGroupName.toLowerCase().includes('capitec')) ?? banks[0];
  if (!bank) { console.log('\nNo banks returned — cannot test payout'); return; }

  const PRIVATE_KEY   = '20ed3f55d3e86a6fa5f7393c543308ef';
  const ENC_KEY       = 'm7Kp9Xv2Qe6Tt4Rz8Ns3Ld5H';
  const NOTIFY_URL    = 'https://securex-btit.onrender.com/securex/payout-notification';
  const amount        = 1.00; // R1 test payout
  const amountCents   = Math.round(amount * 100);
  const merchantRef   = `SX-TEST-${Date.now()}`.slice(0, 20);
  const customerRef   = 'SX-TEST';
  const accountNumber = '1055374116'; // Capitec Business test account
  const branchCode    = bank.universalBranchCode;
  const bankGroupId   = bank.bankGroupId;

  const encryptedAccount = encryptAccountNumber(accountNumber, amountCents, ENC_KEY);
  const hash = buildHash(SITE_CODE, amount, merchantRef, customerRef, false, NOTIFY_URL, bankGroupId, encryptedAccount, branchCode, PRIVATE_KEY);

  const body = {
    siteCode: SITE_CODE,
    amount,
    merchantReference: merchantRef,
    customerBankReference: customerRef,
    isRtc: false,
    notifyUrl: NOTIFY_URL,
    bankingDetails: { bankGroupId, accountNumber: encryptedAccount, branchCode },
    hashCheck: hash,
  };

  console.log(`\nPOST /requestpayout`);
  console.log('  merchantReference:', merchantRef);
  console.log('  bank:', bank.bankGroupName, '| branchCode:', branchCode);
  console.log('  amount: R', amount);

  const res = await fetch(`${BASE_URL}/requestpayout`, {
    method: 'POST',
    headers: { SiteCode: SITE_CODE, ApiKey: API_KEY, 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  const raw = await res.text();
  console.log(`  → ${res.status}`, raw ? JSON.parse(raw) : '');
}

// ── Run ──────────────────────────────────────────────────────────────────────
(async () => {
  console.log('=== Ozow Payout Staging Test ===');
  console.log('SiteCode:', SITE_CODE);
  console.log('BaseUrl: ', BASE_URL);

  await getTestConfig();
  const banks = await getAvailableBanks();
  await requestPayout(banks);
})();
