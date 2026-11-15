# Sandboxes: agents check their own work

With the sandbox on, the technical SEO, analyst, SEO, GEO, content and strategist agents get a
`run_code` tool. They use it to run short Python 3.12 or Node.js 22 programs: parsing their JSON-LD
and XML, testing scripts and regexes, recomputing numbers, transforming data they were given. Mistakes
get caught before work reaches you, which means less of your attention. A matching skill tells them to
verify before saving.

## Isolation and cost

Each run gets its own **throwaway container**, created through the Docker Engine API. Podman serves
the same API, so the harness needs no CLI. Each run takes about 0.75 s on Alpine images, and the
container is always force-removed afterwards.

| Guardrail | Default |
|---|---|
| Network | **none** (no internet, no LAN) |
| Root filesystem | read-only; only `/work` and `/tmp` (64 MB tmpfs each) are writable |
| User | `nobody` (65534); all Linux capabilities dropped; `no-new-privileges` |
| Memory / CPU / processes | 256 MB (no swap) / 0.5 CPU / 64 processes |
| Time | 20 s by default, 60 s maximum; stopped and removed when time is up |
| Concurrency | 2 sandboxes at once (a bulkhead, so agents can't swamp the PC) |
| Quota | 200 runs per agency per day |
| Output / code size | 32 KB / 64 KB |
| Audit | every run is a `sandbox.run` event: job, language, exit code, duration, code hash |

## Turning it on

**Docker** (Linux or macOS): add to `harness.env`:
```
Harness__Sandbox__Mode=container
Harness__Sandbox__Engine=unix:///var/run/docker.sock
```
and mount the socket into the container with `-v /var/run/docker.sock:/var/run/docker.sock`.

**Podman on Windows:** pass the machine's API socket to the start script:
```powershell
.\scripts\host.ps1 -SandboxSocket /run/user/1000/podman/podman.sock
```
The harness container must be allowed to use that socket. If it can't, the dashboard's Skills tab and
`GET /v1/sandbox` say so, and agents carry on without the tool.

**A hosted or self-hosted sandbox service** (`Mode=remote`): set `Harness__Sandbox__RemoteUrl` (and
`RemoteApiKeyEnv` for a bearer key). The service must accept
`POST {language, code, timeout_seconds}` and return `{exit_code, stdout, stderr, timed_out}`. Put a
thin adapter in front of any provider whose API differs.

## Notes

- Access to the engine socket is powerful. Only the harness uses it, and it only creates containers
  with the locked-down settings above. Don't expose the socket anywhere else.
- Images are pulled once on first use. That pull is the only time the engine touches the network for
  sandboxes.
- Turn it off with `Harness__Sandbox__Mode=off` (the default). The tool then disappears from every
  agent automatically.

## Lessons from DeepSeek (November 2026)

DeepSeek hasn't open-sourced an agent sandbox. It published the design of its in-house one, **DSec**
("DeepSeek Elastic Compute", arXiv 2609.22978), which offers four isolation levels behind one SDK:
function call, container, microVM and full VM. It reportedly runs millions of sandboxes a day. We
read about it through secondary coverage only, because the paper itself was blocked from our network.

What we take from it, for a single PC first:

1. **Sandbox self-defence (done).** DeepSeek's agents attacked their sandboxes during training: they
   killed helper processes, deleted system binaries and filled disks. Here the root filesystem is
   read-only, the tmpfs and `fsize` caps stop the disk filling, `nofile` caps open files, output is
   capped, and no helper runs inside the sandbox.
2. **Warm pool (next).** Keep a few locked-down containers started and idle. Run code in one with
   `exec`, then throw it away and refill the pool in the background. That hides the ~0.75 s cold
   start. Never reuse a container.
3. **Stronger isolation for untrusted work (optional).** Use gVisor (`runsc`, Apache-2.0) as the
   container runtime.
4. **Later, in the cloud:** Firecracker microVMs (Apache-2.0) with snapshot/restore, shared read-only
   base images with a copy-on-write layer per run, pausing idle sandboxes, and spreading new sandboxes
   across machines.
