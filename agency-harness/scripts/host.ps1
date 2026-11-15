# Build and run the Agency Harness with Podman on Windows (PowerShell).
# Usage:  .\scripts\host.ps1            build + (re)start on http://localhost:8080
#         .\scripts\host.ps1 -Tunnel    ...and open a public HTTPS address with Cloudflare
param([switch]$Tunnel, [switch]$SkipBuild, [int]$Port = 8080, [string]$Cli = "podman", [string]$Image = "agency-harness:latest")
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)
$Name = "agency-harness"; $EnvFile = "harness.env"

if (-not (Get-Command $Cli -ErrorAction SilentlyContinue)) { throw "$Cli is not installed or not on PATH." }

# 1. Secrets live in harness.env (git-ignored), created once with a strong random admin key.
if (-not (Test-Path $EnvFile)) {
  $bytes = New-Object byte[] 20; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
  $adminKey = "adm_" + (($bytes | ForEach-Object { $_.ToString("x2") }) -join "")
  @(
    "# Agency Harness settings. Keep this file private.",
    "Harness__AdminKey=$adminKey",
    "# Add your Claude API key to have real Claude agents (otherwise a simulated runner is used):",
    "# ANTHROPIC_API_KEY=sk-ant-..."
  ) | Set-Content -Encoding ascii $EnvFile
  Write-Host "Created $EnvFile with a new admin key."
}

# 2. Build the image.
if (-not $SkipBuild) { Write-Host "Building $Image..."; & $Cli build -t $Image . ; if ($LASTEXITCODE) { throw "Build failed." } }

# 3. (Re)start the container. Data stays in the named volume.
& $Cli volume inspect agency-harness-data *> $null; if ($LASTEXITCODE) { & $Cli volume create agency-harness-data | Out-Null }
& $Cli rm -f $Name *> $null
& $Cli run -d --name $Name --restart=unless-stopped -p "${Port}:8080" -v agency-harness-data:/data --env-file $EnvFile $Image | Out-Null
if ($LASTEXITCODE) { throw "Could not start the container." }

Write-Host -NoNewline "Starting"
$ready = $false
for ($i = 0; $i -lt 40; $i++) {
  try { Invoke-RestMethod "http://localhost:$Port/health/ready" | Out-Null; $ready = $true; break } catch { Write-Host -NoNewline "."; Start-Sleep 1 }
}
if (-not $ready) { Write-Host ""; throw "Not healthy yet. Check: $Cli logs $Name" }
Write-Host " ready."

$adminKey = ((Get-Content $EnvFile | Where-Object { $_ -like "Harness__AdminKey=*" }) -split "=", 2)[1]
Write-Host @"

Agency Harness is running at http://localhost:$Port  (MCP: http://localhost:$Port/mcp)

Create your agency's API key (shown once, save it):
  Invoke-RestMethod -Method Post http://localhost:$Port/admin/tenants -Headers @{ 'X-Admin-Key' = '$adminKey' } -ContentType 'application/json' -Body '{"name":"My Agency"}'

Logs:   $Cli logs -f $Name
Stop:   $Cli stop $Name
"@

# 4. Optional public HTTPS address via a free Cloudflare quick tunnel (no account needed).
if ($Tunnel) {
  if (-not (Get-Command cloudflared -ErrorAction SilentlyContinue)) { throw "Install cloudflared first: winget install --id Cloudflare.cloudflared" }
  Write-Host "`nOpening a public tunnel. Look for the https://....trycloudflare.com address below. Ctrl+C closes it."
  cloudflared tunnel --url "http://localhost:$Port"
}
