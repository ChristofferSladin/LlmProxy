#!/usr/bin/env bash
#
# infra/setup-oidc.sh
#
# ONE-TIME, HUMAN-RUN SETUP SCRIPT. Not part of any automated pipeline; nothing in
# .github/workflows/ invokes this. Run it once, manually, from a machine with the Azure CLI
# (`az`) logged in as a user who can create app registrations and assign roles (typically an
# Owner or User Access Administrator on the target subscription/resource group).
#
# Purpose: create an Azure AD app registration with a federated credential trusting GitHub
# Actions' OIDC token for this repo's main branch, so deploy.yml (azure/login@v2) can
# authenticate without any long-lived client secret stored in GitHub. Then grant that app
# just enough Azure RBAC to deploy this app's code (not full subscription Contributor).
#
# After running, put the three printed values into the GitHub repo's
# Settings -> Secrets and variables -> Actions -> Variables (not Secrets — none of these three
# values are secret; they are identifiers, and federated-credential trust is what actually
# gates access):
#   AZURE_CLIENT_ID
#   AZURE_TENANT_ID
#   AZURE_SUBSCRIPTION_ID
# ...plus AZURE_WEBAPP_NAME (the `siteName` this infra produces, e.g. "llmproxy-app" for the
# default `appName=llmproxy` — set manually, not printed by this script).
#
# Safe to re-run: each `az ad app federated-credential create` / role assignment call is
# idempotent-ish (Azure will error on an exact duplicate name, which is a safe no-op signal,
# not data loss).
#
# PREREQUISITE: the resource group ($RESOURCE_GROUP) must already exist — the role assignment
# in step 3 is scoped to it and fails with ResourceGroupNotFound otherwise. Create it first:
#   az group create --name llmproxy-rg --location swedencentral

set -euo pipefail

# ---- Edit these before running -------------------------------------------------------------

APP_DISPLAY_NAME="llmproxy-github-oidc"
GITHUB_REPO="ChristofferSladin/LlmProxy"     # owner/repo, confirmed via `git remote -v`
GITHUB_BRANCH="main"
RESOURCE_GROUP="llmproxy-rg"                 # the resource group infra/main.bicep deploys into

# ---- 1. Create (or reuse) the app registration ----------------------------------------------

echo "== Creating app registration: $APP_DISPLAY_NAME =="
APP_ID=$(az ad app create --display-name "$APP_DISPLAY_NAME" --query appId -o tsv)
echo "Created app registration, appId (client-id) = $APP_ID"

# A service principal is required for role assignment (the app registration alone isn't
# assignable).
echo "== Creating service principal for the app =="
az ad sp create --id "$APP_ID" >/dev/null || true

# ---- 2. Create the federated credential trusting GitHub Actions OIDC for main pushes --------

echo "== Creating federated credential (repo: $GITHUB_REPO, ref: refs/heads/$GITHUB_BRANCH) =="
az ad app federated-credential create \
  --id "$APP_ID" \
  --parameters "{
    \"name\": \"github-actions-main\",
    \"issuer\": \"https://token.actions.githubusercontent.com\",
    \"subject\": \"repo:${GITHUB_REPO}:ref:refs/heads/${GITHUB_BRANCH}\",
    \"description\": \"GitHub Actions OIDC — deploy.yml on push to ${GITHUB_BRANCH}\",
    \"audiences\": [\"api://AzureADTokenExchange\"]
  }"

# ---- 3. Assign a scoped role (resource-group level, not subscription Contributor) -----------

SUBSCRIPTION_ID=$(az account show --query id -o tsv)
TENANT_ID=$(az account show --query tenantId -o tsv)

echo "== Assigning 'Website Contributor' on resource group $RESOURCE_GROUP =="
# "Website Contributor" grants manage rights over App Service sites/plans (start, stop, deploy
# code, configure app settings) without the broader "create/delete anything" surface of
# subscription- or RG-level Contributor. This is deliberately narrower than the ticket's
# fallback suggestion of plain Contributor — the deploy workflow only ever needs to push code
# to an existing web app, never to create/delete the plan or other resources in the group.
# If a future ticket needs the workflow to also (re)provision infra (e.g. `az deployment group
# create` from CI), that would need Contributor instead — not required by T9's scope.
az role assignment create \
  --assignee "$APP_ID" \
  --role "Website Contributor" \
  --scope "/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}"

# ---- 4. Print the values the human needs to put into GitHub repo variables ------------------

echo ""
echo "== Done. Put these into GitHub repo Settings -> Secrets and variables -> Actions -> Variables =="
echo "AZURE_CLIENT_ID       = $APP_ID"
echo "AZURE_TENANT_ID       = $TENANT_ID"
echo "AZURE_SUBSCRIPTION_ID = $SUBSCRIPTION_ID"
echo ""
echo "Also set AZURE_WEBAPP_NAME to the deployed site name (infra/main.bicep 'siteName' var,"
echo "e.g. \"llmproxy-app\" for the default appName='llmproxy')."
