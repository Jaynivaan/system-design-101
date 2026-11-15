# Host the harness on your PC with Podman

This runs the Agency Harness as a container on your own machine. A free Cloudflare Tunnel can then
give it a public HTTPS address, so OpenCode, Claude Code and other apps can reach it from anywhere.

## 1. Get the code

```bash
git clone https://github.com/jaynivaan/system-design-101.git
cd system-design-101
git checkout claude/system-design-agency-nuwwik
cd agency-harness
```

## 2. Start it

**Windows (PowerShell):**

```powershell
.\scripts\host.ps1
```

If Windows blocks the script, run `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` once.

**macOS / Linux / WSL:**

```bash
./scripts/host.sh
```

The script:
1. creates `harness.env` with a random admin key (this file is git-ignored; keep it private)
2. builds the image with Podman
3. starts the container on http://localhost:8080 with a named volume for the data
4. waits until the harness is healthy, then prints the command that creates your agency's API key

Run that printed command and save the `apiKey` it returns. It's shown only once.

**Real Claude agents:** open `harness.env`, uncomment `ANTHROPIC_API_KEY=` and paste your key, then
run the script again. Without a key, a simulated runner does the work, so everything else still
behaves the same.

**Upgrade later:** `git pull`, then run the script again. Your data stays in the
`agency-harness-data` volume.

## 3. Make it reachable from the internet (optional)

Install `cloudflared` (Windows: `winget install --id Cloudflare.cloudflared`, macOS:
`brew install cloudflared`), then run the start script with the tunnel option:

```powershell
.\scripts\host.ps1 -Tunnel        # Windows
./scripts/host.sh --tunnel        # macOS / Linux
```

It prints an address like `https://random-words.trycloudflare.com`. Use that address followed by
`/mcp` in your app configs (see [INTEGRATIONS.md](INTEGRATIONS.md)). It needs no account, but the
address changes every time you start the tunnel and stops working when you close it or turn off
the PC.

**A permanent address:** create a free Cloudflare account, add a domain you own, and set up a
*named tunnel* (`cloudflared tunnel login`, then `cloudflared tunnel create agency-harness`). Point
a hostname such as `harness.yourdomain.com` at `http://localhost:8080`.

## Keep it safe once it's public

- Anyone who has your **admin key** can create tenants. Keep `harness.env` private, and never paste
  the key into chat or commit it.
- Each agency's **API key** only reaches its own data. The harness enforces that on every request,
  and each key is rate-limited.
- A `trycloudflare.com` address is effectively public. Anyone who finds it still needs a valid API
  key for `/v1` and `/mcp`.
- To take it offline, close the tunnel with Ctrl+C, or stop the container with
  `podman stop agency-harness`.

## Troubleshooting

| Problem | Fix |
|---|---|
| `podman: command not found` | Open a new terminal after installing Podman. On Windows/macOS, run `podman machine init` then `podman machine start` once |
| "Not healthy yet" | `podman logs agency-harness` shows the error |
| Port 8080 is in use | Windows: `.\scripts\host.ps1 -Port 9090`. macOS/Linux: `HOST_PORT=9090 ./scripts/host.sh` |
| The container doesn't come back after a reboot | Podman doesn't auto-start containers by itself. Run the script again, or on Linux run `podman generate systemd` |
