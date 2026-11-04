# Dynamic Flow v0 provider contract

## Evidence and verification boundary

This is a documentation-backed contract review, not a production acceptance result. No credentialed
provider calls, source attachment, quote, signing, broadcast, payment, or transfer were performed.
Issue #10's settlement implementation is present via merged PR #12.

Authoritative sources (Dynamic-maintained, retrieved 2026-11-04):

- [API guide](https://www.dynamic.xyz/docs/flow/api.md): base URL, authentication, paths,
  creation/read envelopes, amount units, server-only fields and Solana chain identity.
- [Payment links](https://www.dynamic.xyz/docs/flow/payment-links.md): enterprise enablement,
  hosted origin, CORS registration, fixed payment mode and link capability security.
- [API token permissions](https://www.dynamic.xyz/docs/platform/console/api-token-permissions.md#flow-permissions):
  `flow.write` covers both payment-link creation and server reads (not an invented `flow.read` scope).
- [Access and data](https://www.dynamic.xyz/docs/flow/security.md): server/payer field visibility.
- [Settlements](https://www.dynamic.xyz/docs/flow/settlements.md): delivered evidence and optional fields.
- [Circle USDC contract addresses](https://developers.circle.com/stablecoins/usdc-contract-addresses):
  issuer-authoritative Solana mainnet mint (distinct from Solana devnet USDC).
- [Screening](https://www.dynamic.xyz/docs/flow/screening.md): risk semantics, including unscreened sources.
- [Flow webhooks](https://www.dynamic.xyz/docs/flow/webhooks.md): event envelopes, axes and examples.
- [Webhook setup](https://www.dynamic.xyz/docs/platform/console/webhooks/setup.md): secret and HMAC validation.
- [Public OpenAPI](https://app.dynamic.xyz/api-docs/public-api.yaml): `Flow`, `FlowSettlementData`,
  `FlowExecutionStateEnum`, `FlowSettlementStateEnum`, `FlowRiskStateEnum`,
  `FlowPaymentLinkCreateRequest` and `FlowPaymentLinkCreateResponse`. Its metadata version is `1.0.0`;
  the HTTP API prefix is **v0**, not v1. The generated server GET path uses `{mode}` in the spec;
  the API guide explicitly corrects that segment to the **flow ID**.

The fixtures in `tests/BTCPayServer.Plugins.Fireblocks.Tests/Fixtures/Dynamic/` are public documentation
examples or schema-derived examples, **not captured live responses**:

| Fixture | Origin / changes |
| - | - |
| `hosted-link.json` | API guide's abridged hosted-link response; public example IDs/URL retained |
| `execution.json` | Flow webhooks' source-attached example; formatting only |
| `settlement.json` | Flow webhooks' completed Solana example; placeholder transaction retained |
| `risk.json` | Constructed from documented envelope and risk transition; synthetic IDs |
| `flow-read.json` | Constructed from OpenAPI `Flow`/`FlowSettlementData` and API guide; synthetic memo, destination, transaction IDs; deliberately different USD valuation to prove it is not credited |

None contains a usable credential or proof of a real payment. Documentation-backed fixtures do not
establish that a particular enabled tenant deploys precisely this schema. Capture sanitized,
non-payment responses and signed test deliveries with provider-approved access before acceptance.

## Endpoints, credentials and setup

- Base: `https://app.dynamic.xyz/api/v0`. Environment is a path parameter, not a separate sandbox
  hostname. No alternate regional or staging base has been verified. The API guide documents Base
  Sepolia as the supported testnet, not Solana devnet; this plugin remains Solana mainnet-only.
- Create: `POST /server/{environmentId}/payment-links`, JSON body. Amount is a decimal USD string;
  settlement/destination configuration, memo, `expiresIn` (seconds), and `baseUrl` match the existing
  request factory. Mode is implicitly `payment`, not a request field. Response is `{flow, paymentUrl}`.
- Read: `GET /server/{environmentId}/flow/{flowId}`. Response is the **bare flow**, not `{flow}`.
- Both use `Authorization: Bearer <environment API token>` with **`flow.write`**. Enable enterprise
  Flow/payment links in the chosen environment and register `https://pay.dynamicauth.com` as a CORS
  origin. API tokens stay server-side; the hosted URL/flow ID are payer capabilities, not public IDs.
- Do not use the anonymous `/sdk/.../flow/...` payer read for accounting: it omits server-only
  destination/settlement configuration and settlement transaction hash. Never send a server Bearer
  credential to SDK endpoints.
- Register `https://<btcpay-origin>/stores/<store-id>/fireblocks/webhook` using the Dynamic Console,
  subscribe to execution/settlement/risk updates, and store that webhook's secret separately from the
  API token. Reception requires no Bearer token or API scope. Verify `x-dynamic-signature-256` against
  HMAC-SHA256 of the exact raw body; documented format is `sha256=<hex>`.
- Console registration avoids assuming webhook-management API scopes. If provisioning via API is
  required later, separately verify its administrative permissions; `flow.write` is not asserted to
  grant webhook management.

## Events and state policy

The envelope uses `eventId` (deduplication), `messageId` (delivery), `eventName`, top-level
`environmentId`, and `data.flowId`. `data` contains `axis`, `previousState`, `newState`, `timestamp`,
plus optional `additionalData`. Memo is **not** in webhooks. Verified notifications only trigger a
fresh authenticated server read; event additional data never directly authorizes invoice credit.
Wrong-environment envelopes are rejected. Newly introduced axis states can still wake reconciliation,
but the read must pass all settlement checks.

| Axis | Published vocabulary |
| - | - |
| Execution | `initiated`, `source_attached`, `quoted`, `signing`, `broadcasted`, `source_detected`, `source_confirmed`, `cancelled`, `expired`, `failed` |
| Settlement (guide) | `none`, `settling`, `completed`, `failed` |
| Settlement (additional OpenAPI members) | `routing`, `bridging`, `swapping` |
| Risk (guide) | `unknown`, `cleared`, `blocked` |
| Risk (additional OpenAPI members) | `pending`, `review` |

The broader OpenAPI vocabulary is not silently treated as success. Local accounting requires exact,
case-sensitive `executionState == source_confirmed`, `settlementState == completed`, and
`riskState == cleared`. All missing, differently cased, ambiguous, or new states fail closed.
The store allowlist must additionally include `cleared`; it can restrict, **never widen**, this policy.
The old default `approved` is not a documented Dynamic risk state. Existing stores must explicitly
change it to `cleared`; there is no automatic policy migration or silent reauthorization of old flows.

**Important:** `cleared` is not synonymous with screened. Dynamic sets it without screening the funder
for exchange and deposit-address sources. `unknown` may proceed at Dynamic but cannot receive local
invoice credit. A merchant requiring screening for every payer needs a separate compliance decision;
UI payment-method filtering is not an API enforcement boundary.

## Settlement evidence → accounting

`DynamicFlowResponse` is the v0 wire model; `FlowSnapshot` is an internal normalization model.

| Authoritative field | Validation/accounting use |
| - | - |
| Authenticated environment path + `id` | Immutable attempt environment/Flow binding; the documented read does not contain `environmentId` |
| `mode`, `memo.invoiceId`, `memo.paymentAttemptId` | Require payment mode and exact stored invoice/attempt correlation |
| `settlementConfig`, `destinationConfig` | Compare with immutable creation JSON; exactly one USDC/Solana destination |
| `settlementState` + `executionState` + `riskState` | Require completed / source_confirmed / cleared before reservation |
| `settlement.amount` | **Integer smallest units**, not decimal USDC. Normalize `25000000` → `25` USDC exactly with six decimals; retain atomic string |
| `settlement.toToken`, `settlement.toTokenInfo.symbol/decimals` | Exact USDC mint `EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v`, `USDC`, six decimals |
| `toChainName`, `settlement.toChainId` | Require `SOL` / `101`; never infer delivery network from the payer's source |
| `settlement.toAddress` | Exact original merchant destination, not a quote or browser-supplied value |
| `settlementTxHash` | Delivery transaction identity reserved/idempotently recorded; **never `txHash`**, which identifies source funding |
| `settlement.completedAt` | Optional provider completion timestamp; existing receipt-time fallback remains distinct |
| `settlement.amountUsd`, requested `amount`, `quote.toAmount` | Not settlement token evidence; never substitute for absent delivered amount |

Amount, token metadata, and settlement hash can be absent even for a completed flow. This plugin
fails closed when required evidence is missing: it has **no on-chain verification fallback**. A new
unknown execution or risk state also cannot credit funds. Reservation, exact amount checks, BTCPay
payment insertion, restart recovery, and the fixed 1:1 USDC/USD accounting policy remain unchanged.

Dynamic describes `completed` as delivered and ready for fulfillment. The inspected documentation
does **not** specify a Solana commitment level, minimum confirmation count, fork/reorg guarantee, or
finalized slot. `confirmations` on the flow is not documented as destination finality. Do not present
this integration as independently verifying Solana `finalized` commitment. Obtain a written provider
finality guarantee or separately approve an RPC verification design before real-value acceptance.

The fake under/overpayment exercises remain defensive accounting tests, not claims that Dynamic
payment mode accepts short payments: the guide says short `payment` flows do not settle, whereas
`deposit` mode can settle short. No deposit mode support is added here.

## Remaining provider questions / acceptance prerequisites

1. Confirm deployed tenant schema against the documented v0/OpenAPI differences above; obtain a
   non-payment hosted-link response and authenticated initiated-flow read with a scoped test token.
   Do not attach a source, request a quote or send funds during contract verification.
2. Obtain provider-generated signed execution/risk/settlement **test** deliveries, verify raw-byte HMAC,
   and confirm event-ID stability on retries. The checked-in events are documentation examples only.
3. Confirm destination finality guarantees for Solana and how absent settlement hash, amount, metadata,
   or `toChainName` should be recovered. Current behavior is to leave the attempt unaccounted.
4. Confirm source restrictions/compliance obligations for unscreened exchange/deposit-address funding.
5. Confirm the intended production region/endpoint, enterprise entitlement, CORS origins, token scope,
   webhook secret delivery/rotation, and tenant-specific screening policy with the provider.

### Follow-up end-to-end checklist — unscheduled, separate approval required

- [ ] Resolve the five questions above and retain sanitized tenant evidence; review finality policy.
- [ ] Obtain explicit approval for a separate real-value acceptance ticket, amount ceiling, payer,
      merchant address ownership, network, costs, and stop conditions. Do not schedule it implicitly.
- [ ] Use a disposable compatible BTCPay instance with migrations/backups and isolated credentials;
      enable Flow in the approved environment and configure v0 endpoint, `flow.write`, registered
      hosted CORS origin, USDC/Solana `SOL`/`101` destination and `cleared` risk policy.
- [ ] Register the HTTPS webhook and three event names; confirm secret storage and redacted diagnostics.
- [ ] Check the fixed USD invoice, USDC/USD accounting policy, lifetime, memo and immutable binding.
- [ ] Only in the approved later ticket: perform the bounded payment, inspect delivery evidence and
      finality, reconcile to one BTCPay payment, confirm invoice status and amount, duplicate-event
      idempotence and missed-event recovery. Record differences from documentation.
- [ ] Disable acceptance credentials/endpoints and record residual risks and human sign-off afterward.
