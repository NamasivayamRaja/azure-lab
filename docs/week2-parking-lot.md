# Week 2 Parking Lot (Review on Day 7)

- **Entra Security Group for dev/admin RBAC (`principalType: 'Group'`)**: Replace per-user `openAiDevUserObjectId` / `searchDevUserObjectId` / `contentSafetyDevUserObjectId` / `docIntelligenceDevUserObjectId` with a single Entra Security Group OID so onboarding/offboarding happens in Entra ID with zero Bicep/RBAC churn and avoids the 4,000-role-assignment subscription limit. Estimated cost: \$0.
