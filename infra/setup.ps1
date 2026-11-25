<#
.SYNOPSIS
Prepares Azure and GitHub so the CI can deploy ConvertAPI (see docs/deploy.md). Run it once,
signed in with `az login` and `gh auth login`; running it again is safe.

.DESCRIPTION
Azure: registers the resource providers, creates one resource group per environment, and an app
registration that GitHub Actions signs in as through OIDC (a federated credential per GitHub
environment, so no Azure secret is ever stored). Per resource group it gets Contributor, plus
the right to assign one role only, Key Vault Secrets User, which the Bicep grants the apps.

GitHub: creates the environments, deployable from main only (prod also requires your approval),
their Azure variables, and generated secrets: the PostgreSQL password, a demo API key (only its
hash is deployed) with its webhook secret, and the same key for the smoke test. Existing secrets
are kept; deleting SMOKE_API_KEY generates a new demo key (replacing API_KEYS and WEBHOOK_SECRETS),
deleting POSTGRES_PASSWORD a new database password.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $SubscriptionId,

    [string] $Location = 'francecentral',

    [string] $Repository = 'BileulDevs/Convert-API',

    [string[]] $Environments = @('test', 'prod')
)

$ErrorActionPreference = 'Stop'
$KeyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'
$AppName = 'convertapi-github-deploy'

function Invoke-Native([string] $Command, [Parameter(ValueFromRemainingArguments)] [string[]] $Arguments) {
    # Runs az or gh and stops on failure: native commands do not throw on their own.
    $output = & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Failed: $Command $($Arguments -join ' ')" }
    return $output
}

function New-RandomString([int] $Length, [string] $Alphabet) {
    # Unbiased: bytes that would favour the first letters of the alphabet are drawn again.
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $limit = 256 - (256 % $Alphabet.Length)
    $chars = New-Object System.Text.StringBuilder
    $byte = New-Object byte[] 1
    while ($chars.Length -lt $Length) {
        $generator.GetBytes($byte)
        if ($byte[0] -lt $limit) { [void] $chars.Append($Alphabet[$byte[0] % $Alphabet.Length]) }
    }
    return $chars.ToString()
}

function Get-Sha256Hex([string] $Value) {
    $hash = [System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Value))
    return -join ($hash | ForEach-Object { $_.ToString('x2') })
}

function New-ApiKey {
    # Same format as ConvertApi.Core.ApiKeys.ApiKeyCredential: cvt_<key id>_<secret>.
    $keyId = New-RandomString 8 'abcdefghijklmnopqrstuvwxyz0123456789'
    $key = "cvt_${keyId}_$(New-RandomString 32 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789')"
    return [pscustomobject] @{ Key = $key; KeyId = $keyId; SecretHash = (Get-Sha256Hex $key) }
}

function Set-GitHubSecret([string] $Environment, [string] $Name, [string] $Value) {
    # Read from a file on stdin: the value never shows in a process list, and no trailing newline
    # gets added, which a PowerShell 5.1 pipe would do.
    $file = New-TemporaryFile
    try {
        [System.IO.File]::WriteAllText($file.FullName, $Value)
        cmd /c "gh secret set $Name --env $Environment --repo $Repository < `"$($file.FullName)`""
        if ($LASTEXITCODE -ne 0) { throw "Could not set secret $Name in $Environment" }
    }
    finally {
        Remove-Item $file
    }
}

function Initialize-Azure {
    Invoke-Native az account set --subscription $SubscriptionId | Out-Null
    $tenantId = Invoke-Native az account show --query tenantId --output tsv

    foreach ($provider in 'Microsoft.App', 'Microsoft.OperationalInsights', 'Microsoft.Insights',
        'Microsoft.DBforPostgreSQL', 'Microsoft.KeyVault', 'Microsoft.Storage', 'Microsoft.ManagedIdentity') {
        Invoke-Native az provider register --namespace $provider | Out-Null
    }

    $appId = Invoke-Native az ad app list --display-name $AppName --query '[0].appId' --output tsv
    if (-not $appId) {
        $appId = Invoke-Native az ad app create --display-name $AppName --query appId --output tsv
        Invoke-Native az ad sp create --id $appId | Out-Null
    }
    $principalId = Invoke-Native az ad sp show --id $appId --query id --output tsv

    $existingCredentials = Invoke-Native az ad app federated-credential list --id $appId --query '[].name' --output tsv
    foreach ($environment in $Environments) {
        $group = "rg-convertapi-$environment"
        Invoke-Native az group create --name $group --location $Location --output none
        $scope = Invoke-Native az group show --name $group --query id --output tsv

        if ($existingCredentials -notcontains "github-$environment") {
            $credential = @{
                name      = "github-$environment"
                issuer    = 'https://token.actions.githubusercontent.com'
                subject   = "repo:${Repository}:environment:$environment"
                audiences = @('api://AzureADTokenExchange')
            } | ConvertTo-Json -Compress
            $file = New-TemporaryFile
            try {
                Set-Content -Path $file -Value $credential -Encoding ascii
                Invoke-Native az ad app federated-credential create --id $appId --parameters "@$file" | Out-Null
            }
            finally {
                Remove-Item $file
            }
        }

        Invoke-Native az role assignment create --assignee-object-id $principalId --assignee-principal-type ServicePrincipal `
            --role Contributor --scope $scope --output none
        # May only create or remove assignments of Key Vault Secrets User, the role the Bicep grants.
        $condition = "((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR " +
            "(@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {$KeyVaultSecretsUser})) AND " +
            "((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR " +
            "(@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {$KeyVaultSecretsUser}))"
        Invoke-Native az role assignment create --assignee-object-id $principalId --assignee-principal-type ServicePrincipal `
            --role 'Role Based Access Control Administrator' --scope $scope `
            --condition $condition --condition-version 2.0 --output none
    }

    return [pscustomobject] @{ TenantId = $tenantId; ClientId = $appId }
}

function Initialize-GitHub($Azure) {
    $me = Invoke-Native gh api user --jq .id
    $demoKeys = @{}
    foreach ($environment in $Environments) {
        # Only main may deploy: the Azure trust is per environment, so this keeps a pushed branch
        # from editing a workflow to obtain an Azure token. Prod also waits for your approval.
        $settings = @{ deployment_branch_policy = @{ protected_branches = $false; custom_branch_policies = $true } }
        if ($environment -eq 'prod') { $settings.reviewers = @(@{ type = 'User'; id = [int] $me }) }
        $settings | ConvertTo-Json -Compress -Depth 3 | gh api --method PUT "repos/$Repository/environments/$environment" --input - | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not create the $environment environment" }
        $branches = Invoke-Native gh api "repos/$Repository/environments/$environment/deployment-branch-policies" --jq '.branch_policies[].name'
        if ($branches -notcontains 'main') {
            Invoke-Native gh api --method POST "repos/$Repository/environments/$environment/deployment-branch-policies" -f name=main | Out-Null
        }

        foreach ($variable in @{
                AZURE_CLIENT_ID       = $Azure.ClientId
                AZURE_TENANT_ID       = $Azure.TenantId
                AZURE_SUBSCRIPTION_ID = $SubscriptionId
                AZURE_RESOURCE_GROUP  = "rg-convertapi-$environment"
            }.GetEnumerator()) {
            Invoke-Native gh variable set $variable.Key --env $environment --repo $Repository --body $variable.Value | Out-Null
        }

        $existing = Invoke-Native gh secret list --env $environment --repo $Repository --json name --jq '.[].name'
        if ($existing -notcontains 'POSTGRES_PASSWORD') {
            Set-GitHubSecret $environment 'POSTGRES_PASSWORD' (New-RandomString 32 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789')
        }
        if ($existing -notcontains 'SMOKE_API_KEY') {
            $key = New-ApiKey
            $apiKeys = ConvertTo-Json -Compress -InputObject @(@{
                    id = [guid]::NewGuid().ToString(); keyId = $key.KeyId; name = 'Demo and smoke tests'; secretHash = $key.SecretHash
                })
            Set-GitHubSecret $environment 'API_KEYS' $apiKeys
            Set-GitHubSecret $environment 'WEBHOOK_SECRETS' (@{ $key.KeyId = (New-RandomString 40 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789') } | ConvertTo-Json -Compress)
            Set-GitHubSecret $environment 'SMOKE_API_KEY' $key.Key
            $demoKeys[$environment] = $key.Key
        }
    }

    Invoke-Native gh variable set DEPLOY_ENABLED --repo $Repository --body 'true' | Out-Null
    return $demoKeys
}

if ($MyInvocation.InvocationName -ne '.') {
    $azure = Initialize-Azure
    $demoKeys = Initialize-GitHub $azure
    Write-Host "Azure and GitHub are ready: the next push to main deploys to test, then waits for your approval for prod."
    foreach ($entry in $demoKeys.GetEnumerator()) {
        # Shown once, here only: GitHub keeps it as a secret, Azure only its hash.
        Write-Host "Demo API key for $($entry.Key): $($entry.Value)"
    }
}
