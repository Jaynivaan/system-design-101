# Build and run the Agency Harness with Podman on Windows (PowerShell).
# Usage:  .\scripts\host.ps1            build + (re)start on http://localhost:8080
#         .\scripts\host.ps1 -Tunnel    ...and open a public HTTPS address with Cloudflare
#         .\scripts\host.ps1 -HostAddress 172.20.1.5   if the app isn't reachable on localhost
# If localhost doesn't answer (e.g. WSL with localhostForwarding=false), the script finds the
# Podman machine's WSL IP address and uses that for the health check and the tunnel.
param([switch]$Tunnel, [switch]$SkipBuild, [int]$Port = 8080, [string]$Cli = "podman", [string]$Image = "agency-harness:latest",
      [string]$HostAddress = "localhost", [string]$MachineName = "podman-machine-default")
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

function Test-Ready([string]$hostName) {
  try { Invoke-RestMethod -TimeoutSec 3 "http://${hostName}:$Port/health/ready" | Out-Null; return $true } catch { return $false }
}
function Get-MachineIp {
  # The Podman machine is a WSL distro; its eth0 address is reachable from Windows even when
  # localhost forwarding is turned off.
  try {
    $line = wsl -d $MachineName -- ip -4 -o addr show eth0 2>$null
    if ($line -match 'inet (\d+\.\d+\.\d+\.\d+)') { return $Matches[1] }
  } catch {}
  return $null
}

function Connect-PodmanMachine {
  # Make sure the podman CLI can reach its machine. On some Windows setups (WSL with
  # localhostForwarding=false) the CLI's ssh connection to 127.0.0.1 never works, so we point it
  # at the machine's WSL address instead, reusing the port, user, socket and key it already has.
  & $Cli info *> $null; if ($LASTEXITCODE -eq 0) { return }
  # Only try `machine start` when the machine isn't already running: on PCs where the CLI can't
  # reach 127.0.0.1, start fails slowly (30-60 s) even though the machine is up.
  $state = ""
  try { $state = (& $Cli machine inspect $MachineName --format "{{.State}}" 2>$null | Out-String).Trim() } catch {}
  if ($state -ne "running") {
    Write-Host "Podman isn't answering; starting its machine..."
    & $Cli machine start $MachineName *> $null
    & $Cli info *> $null; if ($LASTEXITCODE -eq 0) { return }
  }
  $ip = Get-MachineIp
  if (-not $ip) { throw "Podman can't reach its machine and its WSL address wasn't found. Try: $Cli machine start" }
  try { $conns = & $Cli system connection list --format json | ConvertFrom-Json } catch { $conns = @() }
  $c = @($conns | Where-Object { $_.Default }) + @($conns) | Select-Object -First 1
  if (-not $c -or -not $c.URI) { throw "Podman can't reach its machine, and no connection details were found. See docs/HOSTING.md (Troubleshooting)." }
  $u = [Uri]$c.URI
  $env:CONTAINER_HOST = "ssh://$($u.UserInfo)@${ip}:$($u.Port)$($u.AbsolutePath)"
  if ($c.Identity) { $env:CONTAINER_SSHKEY = $c.Identity }
  & $Cli info *> $null
  if ($LASTEXITCODE) { throw "Podman still can't reach its machine at $ip. See docs/HOSTING.md (Troubleshooting)." }
  Write-Host "Connected to the Podman machine at $ip (localhost forwarding is off on this PC)."
}

Connect-PodmanMachine

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
  if (Test-Ready $HostAddress) { $ready = $true; break }
  # After a few tries on localhost, try the Podman machine's own address.
  if ($i -eq 8 -and $HostAddress -eq "localhost") {
    $ip = Get-MachineIp
    if ($ip) { Write-Host -NoNewline " (localhost not answering, trying $ip)"; $HostAddress = $ip }
  }
  Write-Host -NoNewline "."; Start-Sleep 1
}
if (-not $ready) { Write-Host ""; throw "Not healthy yet. Check: $Cli logs $Name   (if localhost isn't forwarded, pass -HostAddress <Podman machine IP>)" }
Write-Host " ready."
if ($HostAddress -ne "localhost") { Write-Host "Note: reached through $HostAddress. This address can change when WSL restarts." }

Write-Host @"

Agency Harness is running at http://${HostAddress}:$Port  (MCP: http://${HostAddress}:$Port/mcp)
Dashboard: open that address in a browser and sign in with your agency's API key.

Create your agency's API key (shown once, save it). Your admin key is the Harness__AdminKey line in ${EnvFile}:
  `$admin = ((Get-Content $EnvFile | Where-Object { `$_ -like 'Harness__AdminKey=*' }) -split '=', 2)[1]
  Invoke-RestMethod -Method Post http://${HostAddress}:$Port/admin/tenants -Headers @{ 'X-Admin-Key' = `$admin } -ContentType 'application/json' -Body '{"name":"My Agency"}'

Logs:   $Cli logs -f $Name
Stop:   $Cli stop $Name
"@

# 4. Optional public HTTPS address via a free Cloudflare quick tunnel (no account needed).
if ($Tunnel) {
  if (-not (Get-Command cloudflared -ErrorAction SilentlyContinue)) { throw "Install cloudflared first: winget install --id Cloudflare.cloudflared" }
  Write-Host "`nOpening a public tunnel. Look for the https://....trycloudflare.com address below. Ctrl+C closes it."
  cloudflared tunnel --url "http://${HostAddress}:$Port"
}
