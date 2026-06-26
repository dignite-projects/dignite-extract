---
name: run
description: Start the Vault Extract dev environment — SQL Server reachability check, .NET host at https://localhost:44348, Angular SPA at http://localhost:4200 — and verify both are healthy. Also covers stopping the stack and regenerating Angular client proxies.
---

# Run: Start and Verify the Vault Extract Dev Stack

## Connection targets (read from real appsettings files)

| Environment | `ConnectionStrings:Default` |
|---|---|
| Base (`appsettings.json`) | `Server=(LocalDb)\MSSQLLocalDB;Database=Extract;Trusted_Connection=True;TrustServerCertificate=true` |
| Development (`appsettings.Development.json`) | `Server=REDACTED-DB-HOST;User ID=REDACTED;...;Database=REDACTED;Trusted_Connection=False;TrustServerCertificate=true` |

`launchSettings.json` sets `ASPNETCORE_ENVIRONMENT=Development`, so the **Development override is always active** during `dotnet run`. The active target is the remote SQL Server at `REDACTED-DB-HOST:1433`.

The base appsettings LocalDB target (`(LocalDb)\MSSQLLocalDB`) is only used when the `Development` override is absent (e.g. custom `--environment` flag or a CI environment that does not load the Development file).

---

## Step 1 — Verify SQL Server reachability

**Development environment (normal case): remote SQL Server**

```powershell
Test-NetConnection REDACTED-DB-HOST -Port 1433
```

Expect `TcpTestSucceeded : True`. If this fails, check VPN/network access before proceeding — the host will fail to start with a DB connection error.

**Base fallback: LocalDB**

If running without the Development override (unusual), the target is `(LocalDb)\MSSQLLocalDB`. LocalDB uses a local named pipe; there is no TCP port to check. Verify the instance exists with:

```powershell
sqllocaldb info MSSQLLocalDB
```

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

**TLS note.** The Angular dev server calls the host directly (via `environment.ts` — there is no Angular proxy config file). Node-side tooling skips TLS verification via `NODE_TLS_REJECT_UNAUTHORIZED=0`, which is already set in `.Codex/settings.local.json`. Do not add a proxy config file to work around TLS; the env var is the correct mechanism.

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
| DB reachability (Dev) | `Test-NetConnection REDACTED-DB-HOST -Port 1433` | `TcpTestSucceeded : True` |
| Start host | `dotnet run --project host/src` | Binds `https://localhost:44348` |
| Health check | `GET https://localhost:44348/health-status` | HTTP 200 |
| Swagger | `https://localhost:44348/swagger` | Swagger UI loads |
| Start Angular | `cd angular && npm start` | `http://localhost:4200` |
| Generate proxies | `cd angular && npm run generate-proxy` | Proxy files updated |
| Stop host | `Get-NetTCPConnection -LocalPort 44348 …` | Process killed |
| Stop Angular | `Get-NetTCPConnection -LocalPort 4200 …` | Process killed |
