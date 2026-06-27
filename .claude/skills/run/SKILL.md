---
name: run
description: Start the Vault Extract dev environment — SQL Server reachability check, .NET host at https://localhost:44348, Angular SPA at http://localhost:4200 — and verify both are healthy. Also covers stopping the stack and regenerating Angular client proxies.
---

# Run: Start and Verify the Vault Extract Dev Stack

## Connection targets (resolved from config at runtime — never hardcode the remote target here)

| Environment | `ConnectionStrings:Default` source |
|---|---|
| Base (`appsettings.json`, tracked) | `Server=(LocalDb)\MSSQLLocalDB;Database=Extract;Trusted_Connection=True;TrustServerCertificate=true` |
| Development override | Provided by `appsettings.secrets.json` (**gitignored, per-developer**) — points at a remote SQL Server. The literal host / user / database are intentionally **not** stored in this skill or any tracked file. |

`launchSettings.json` sets `ASPNETCORE_ENVIRONMENT=Development`, so the **Development override is always active** during `dotnet run`. The effective `ConnectionStrings:Default` therefore comes from `appsettings.secrets.json` when that file is present.

The base appsettings LocalDB target (`(LocalDb)\MSSQLLocalDB`) is used only when the secrets override is absent (e.g. a fresh checkout without `appsettings.secrets.json`, a custom `--environment` flag, or CI).

---

## Step 1 — Verify SQL Server reachability

Resolve the active SQL `host:port` **from the gitignored secrets file at runtime** — no literal target is committed to this skill:

```powershell
$secrets = "host/src/appsettings.secrets.json"
if (Test-Path $secrets) {
    # Development override present → remote SQL Server
    $cs = (Get-Content $secrets -Raw | ConvertFrom-Json).ConnectionStrings.Default
    if ($cs -match 'Server=([^;,]+)(?:,(\d+))?') {
        $sqlHost = $Matches[1]
        $sqlPort = if ($Matches[2]) { $Matches[2] } else { 1433 }
        Test-NetConnection $sqlHost -Port $sqlPort   # expect TcpTestSucceeded : True
    }
} else {
    # No override → base LocalDB target (named pipe, no TCP port to check)
    sqllocaldb info MSSQLLocalDB
}
```

For the remote case, expect `TcpTestSucceeded : True`. If it fails, check VPN/network access before proceeding — the host will fail to start with a DB connection error.

---

## Step 2 — Start the .NET host

The host project lives at `host/src/Dignite.Vault.Extract.Host.csproj` (directly inside `host/src/`).

```powershell
dotnet run --project host/src
```

Or, to pin the environment explicitly:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project host/src
```

The host binds to `https://localhost:44348` (confirmed in both `launchSettings.json` and `appsettings.json` `App.SelfUrl`).

**Wait for readiness.** Poll until the health endpoint responds HTTP 200:

```powershell
do {
    Start-Sleep -Seconds 2
    try { $r = Invoke-WebRequest -Uri "https://localhost:44348/health-status" -SkipCertificateCheck -UseBasicParsing -ErrorAction Stop }
    catch { $r = $null }
} until ($r -and $r.StatusCode -eq 200)
Write-Host "Host is up."
```

Swagger UI is available at `https://localhost:44348/swagger` (served via `UseAbpSwaggerUI`).

---

## Step 3 — Start the Angular SPA

The workspace root is `angular/`. The Nx app name is `host`. The `package.json` `start` script runs `nx serve host`.

```powershell
Set-Location angular
npm start
```

Alternative (equivalent, bypasses the npm script wrapper):

```powershell
npx nx serve host
```

The SPA dev server listens on `http://localhost:4200`.

**TLS note.** The Angular dev server calls the host directly (via `environment.ts` — there is no Angular proxy config file). Node-side tooling skips TLS verification via `NODE_TLS_REJECT_UNAUTHORIZED=0`, which is already set in `.claude/settings.local.json`. Do not add a proxy config file to work around TLS; the env var is the correct mechanism.

**Verify Angular is up:**

```powershell
Invoke-WebRequest -Uri "http://localhost:4200" -UseBasicParsing -ErrorAction Stop
```

---

## Stopping the stack

**Kill the host by port (Windows PowerShell):**

```powershell
Get-NetTCPConnection -LocalPort 44348 -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess |
    ForEach-Object { Stop-Process -Id $_ -Force }
```

**Kill the Angular dev server by port:**

```powershell
Get-NetTCPConnection -LocalPort 4200 -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess |
    ForEach-Object { Stop-Process -Id $_ -Force }
```

Do not use `pkill` — it is not available on Windows / Git Bash.

---

## Regenerating Angular client proxies

Requires the host running at `https://localhost:44348`.

```powershell
Set-Location angular
npm run generate-proxy
```

This runs:

```
nx g @abp/nx.generators:generate-proxy --module=vault-extract --apiName=Default --source=host --target=vault-extract --url=https://localhost:44348 --serviceType=application --no-interactive
```

(exact script from `angular/package.json`)

---

## Quick-reference checklist

| Step | Command | Expected result |
|---|---|---|
| DB reachability (Dev) | Resolve host:port from `appsettings.secrets.json`, then `Test-NetConnection` | `TcpTestSucceeded : True` |
| Start host | `dotnet run --project host/src` | Binds `https://localhost:44348` |
| Health check | `GET https://localhost:44348/health-status` | HTTP 200 |
| Swagger | `https://localhost:44348/swagger` | Swagger UI loads |
| Start Angular | `cd angular && npm start` | `http://localhost:4200` |
| Generate proxies | `cd angular && npm run generate-proxy` | Proxy files updated |
| Stop host | `Get-NetTCPConnection -LocalPort 44348 …` | Process killed |
| Stop Angular | `Get-NetTCPConnection -LocalPort 4200 …` | Process killed |
