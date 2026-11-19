# ADR 003: API Authentication and Per-Resource Authorization Model

- **Status:** Accepted (Cloud Week 1, Day 4)
- **Context:** The API exposes player redemption endpoints (`POST /offers/{id}/redeem`), event document management (`events/{eventId}/documents`), and event Q&A/assistant endpoints (`/ask`, `/assistant`). Previously, `RedeemRequestDto` trusted a caller-supplied `UserId` in the JSON body (a textbook BOLA/IDOR vulnerability), and endpoints lacked token validation.

## Decision
1. **In-Code JWT Bearer Validation (`Microsoft.Identity.Web`) over App Service Easy Auth:**
   - Configure `AddMicrosoftIdentityWebApiAuthentication` in `Program.cs` against Microsoft Entra ID (`api-azurelab-dev`) with `requestedAccessTokenVersion = 2`.
   - Omit `AzureAd:Audience` so `Microsoft.Identity.Web` validates the bare `ClientId` GUID emitted in Entra v2 `aud` claims.
2. **Unified `ApiAccess` Policy (Interactive Users + Daemons):**
   - Require authenticated callers to present either delegated scope `scp = api.access` (interactive CLI/SPA users via pre-authorized Azure CLI client `04b07795-8ddb-461a-bbee-02f9e1bf7b46`) or app role `roles = Api.Service` (service-to-service daemons).
3. **Eliminate BOLA/IDOR by Binding Identity from Token Claims:**
   - Remove `UserId` from `RedeemRequestDto`; extract the caller's immutable Entra object ID (`oid` / `http://schemas.microsoft.com/identity/claims/objectidentifier`) inside the controller.
4. **Resource-Level Authorization via `EventEnrollment` ACL Table:**
   - Add `EventEnrollments (EventId, UserId)` in SQL.
   - Gate `/events/{eventId}/ask` and `/assistant` on both `ApiAccess` and verified membership in `EventEnrollments` (returning `403 Forbidden` when unenrolled, while `Roles = Api.Service` bypasses enrollment for system tasks).
   - Keep `/health/live` and `/health/ready` `[AllowAnonymous]` for load balancer and synthetic probes.
5. **Public Repo Tenant/Client ID Hygiene:**
   - Keep real `TenantId` and `ClientId` out of `appsettings.json` and `dev.bicepparam`; inject via `dotnet user-secrets` locally and `readEnvironmentVariable()` + GitHub Repository Variables in CI/CD.

## Alternatives Considered
- **App Service Authentication (Easy Auth):** Rejected as primary mechanism. Easy Auth cannot run locally in `dotnet run` / Docker, making integration and k6 parity testing harder, and cannot enforce database-backed per-event ACLs (`EventEnrollment`).
- **Passing `UserId` in Request Body or Custom Header:** Rejected. Allows any authenticated user to redeem offers or query events on behalf of another user (BOLA/IDOR).

## Consequences
- **Positive:** Identical auth pipeline in local Kestrel and Azure App Service; zero client secrets required for developer CLI token acquisition (`az account get-access-token --scope api://<id>/api.access`); strict tenant + per-event isolation ready for Week 2 AI Search security trimming.
- **Negative / Operational Trade-offs:**
  - Every load test (`k6`) and synthetic business test must acquire and pass a valid Entra Bearer token.
  - Missing `AZURE_TENANT_ID` / `AZURELAB_API_CLIENT_ID` in CI environment variables causes Bicep to write empty `AzureAd__*` settings, failing startup with `IDW10106` (guarded in `infra.yml`).
