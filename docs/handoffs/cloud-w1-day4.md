================================================================================
HANDOFF BLOCK FOR DAY 5 — OBSERVABILITY, ALERTS & PRODUCTION HARDENING
================================================================================

1. ENTRA ID APP REGISTRATIONS (ZERO SECRETS)
- Tenant ID:             `<AZURE_TENANT_ID>`
- API Registration:      `api-azurelab-dev`
  * Application (Client) ID: `<AZURELAB_API_CLIENT_ID>`
  * Identifier URI:          `api://<AZURELAB_API_CLIENT_ID>`
  * Token Version:           `requestedAccessTokenVersion = 2` (v2.0 JWTs; `aud` = bare Client ID)
  * Delegated Scope:         `api.access` (pre-authorized for Azure CLI `04b07795-8ddb-461a-bbee-02f9e1bf7b46` and `cli-azurelab-test`)
  * App Role (Application):  `Api.Service` (`allowedMemberTypes = ["Application"]`)
- Test Client Registration: `cli-azurelab-test`
  * Application (Client) ID: `<AZURELAB_TEST_CLIENT_ID>`
  * Type:                    Public client (`isFallbackPublicClient = true`, `http://localhost`, zero secrets)

2. AUTH ARCHITECTURE & POLICIES
- Choice:                In-code JWT Bearer (`Microsoft.Identity.Web` v4.18.0) over App Service Easy Auth
                         (reason: local dev parity, fine-grained policies, per-event ACLs, OBO readiness).
- Middleware Order:      `app.UseHttpsRedirection()` -> `app.UseAuthentication()` -> `app.UseAuthorization()`
                         (active in all environments; `DevAuthenticationHandler` no longer registered).
- Caller Identity:       `ClaimsPrincipalExtensions.GetCallerObjectId()` reads `oid` (`http://schemas.microsoft.com/identity/claims/objectidentifier`),
                         falling back to `oid` then `NameIdentifier`. Never trusts `UserId` from request body.
- Per-Event ACL:         `EventEnrollment` table (`EventId`, `UserId` composite PK, EF migration `AddEventEnrollments`)
                         via `IEventAccessService` / `EfEventAccessService`. Callers with `Api.Service` role bypass per-user ACLs.

| Policy Name | Requirement |
|---|---|
| `ApiAccess` | Authenticated Entra v2 JWT AND (`scp` contains `api.access` OR `roles` contains `Api.Service`) |
| Anonymous | No authentication required |

3. ENDPOINT-TO-POLICY TABLE
| Endpoint | Policy / AuthZ | Per-Event Check |
|---|---|---|
| `GET /health`, `/health/live`, `/health/ready` | Anonymous | No (required for App Service probes & slot warm-up) |
| `POST /offers/{offerId:guid}/redeem` | `ApiAccess` | No (`userId` derived from token `oid`; `RedeemRequestDto.UserId` removed) |
| `POST /events/{eventId:guid}/ask` | `ApiAccess` | Yes (`IEventAccessService.CanAccessEventAsync` -> `403`) |
| `POST /events/{eventId:guid}/assistant` *(Dev)* | `ApiAccess` | Yes (`IEventAccessService.CanAccessEventAsync` -> `403`, `AllowedEventIds` from DB) |
| `POST /proposals/{proposalId:guid}/confirm` *(Dev)* | `ApiAccess` | Ownership via `userId` (`oid`) in `ProposalConfirmationService` |
| `EventDocumentsController` (`POST`, `download-sas`, `upload-sas`, `complete`) | `ApiAccess` | No (Operator/employee workflow; valid token + `ApiAccess` required) |

4. CONFIG KEYS & BICEP APP SETTINGS ADDED
- Package added:         `Microsoft.Identity.Web` (`4.18.0`) in `AzureLabs.Api/AzureLabs.Api.csproj`.
- `appsettings.json`:    `AzureAd:Instance` (`https://login.microsoftonline.com/`), `AzureAd:TenantId` (`""`), `AzureAd:ClientId` (`""`).
- `appsettings.Development.json`: Commented `AzureAd` placeholders; real IDs stored in `dotnet user-secrets`.
- Note on `Audience`:    Omitted intentionally—Entra v2 tokens set `aud` to the bare `ClientId` GUID, and omitting `Audience` lets `Microsoft.Identity.Web` accept both `<ClientId>` and `api://<ClientId>`.
- Bicep (`appservice.bicep` / `main.bicep` / `dev.bicepparam`):
  * Non-sticky `apiBaseSettings` (both `production` and `staging` slots): `AzureAd__Instance` (`environment().authentication.loginEndpoint`), `AzureAd__TenantId`, `AzureAd__ClientId`.
  * `dev.bicepparam` reads `AZURE_TENANT_ID` and `AZURELAB_API_CLIENT_ID` via `readEnvironmentVariable(...)`.
- GitHub Workflow (`infra.yml`): Added `AZURE_TENANT_ID` and `AZURELAB_API_CLIENT_ID` to `env:` (and `AZURELAB_API_CLIENT_ID` in GitHub Repo Variables).

5. TEST MATRIX RESULTS (LOCAL & AZURE)
| Case | Target | Expected | Observed | Notes |
|---|---|---|---|---|
| 1. No token | Local `POST /offers/{id}/redeem` | `401` | `401` | Rejected by `JwtBearer` |
| 2. Health anonymous | Local `GET /health` | `200` | `200` | Probe & warm-up intact |
| 3. Token for different API (Graph) | Local `POST /offers/{id}/redeem` | `401` | `401` | `aud` mismatch (`00000003-...`) |
| 4. Tampered signature | Local `POST /offers/{id}/redeem` | `401` | `401` | Signature validation failed |
| 5. Valid token, another user's event | Local `POST /events/{other}/ask` | `403` | `403` | Passed `ApiAccess`; denied by `EfEventAccessService` |
| 6. Azure: No token | Azure `POST /offers/{id}/redeem` | `401` | `401` | Live `app-azurelab-api-dev-cin-5kaayi` |
| 7. Azure: Health | Azure `GET /health` | `200` | `200` | Live App Service probe |
| 8. Azure: Valid token (`api.access`) | Azure `POST /events/{other}/ask` | `403`/`503` | `503` | Passed `JwtBearer` + `ApiAccess`; fast-failed at `Ai:Enabled=false` before `CanAccessEventAsync` |
================================================================================
