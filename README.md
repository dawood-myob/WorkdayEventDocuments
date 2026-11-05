# WorkdayEventDocuments

A small .NET 8 console app that uses the **Workday Integrations SOAP API** to download files produced by an
integration's events.

Out of the box it is set up for `INT010_Visa_VCF4_Credit_Cards_Inbound_AU`
(Integration System ID `INT010_Visa_Credit_Cards_AU`) and downloads the **VCF `.pgp` files** into one folder per
date the event ran.

## How it works

1. `Get_Integration_Events` lists the events of the integration system (all history, or the last `DaysBack` days).
2. Only events with status `Completed` are kept (`OnlyCompleted`); `CompletedWithWarnings` etc. are skipped.
3. Each integration event has a **parent event**. The `.pgp` files are not on the integration event itself, they are
   attached to the sibling **"Document Retrieval"** event that has the same parent and start time. The app finds that
   sibling with a second `Get_Integration_Events` call over a +/- 1 minute window.
   (The parent event itself cannot be read through the Integrations API - Workday rejects its ID as the wrong type.)
4. The SOAP API only returns document *metadata*. The file bytes are fetched from
   `https://{Host}/ccx/cc-blobitory/{Tenant}/{Document_ID}` using the same credentials.
5. Files are saved with their original name:

```
Downloads/VCF/
   2026-10-30/VCF4_0000030679__03985-30-10-2026-07-56-03.pgp
   2026-10-29/VCF4_0000030679__03984-29-10-2026-07-49-45.pgp
```

The folder date is the **local** date the event started. Files that already exist are skipped, so re-running is safe.

Workday documents expire (roughly 60 days in the tenant tested), so older files return HTTP 404 and are reported as
"not downloadable". This is a Workday retention limit, not an app error.

### Optional: download every event's files

Set `DownloadAllEventFiles` to `true` to save all documents of each event (not only `.pgp`) into
`Downloads/Run_yyyyMMdd_HHmmss/{start}_{eventWid}/`, with an `event_info.txt` (event ID, status, parent WID) per event.

## Configuration

Non-sensitive settings live in `appsettings.json`:

| Setting | Meaning |
|---|---|
| `Host` | `services1.wd105.myworkday.com` (production) or `impl-services1.wd105.myworkday.com` (sandbox) |
| `Tenant` | Workday tenant, e.g. `myob` |
| `ApiVersion` | e.g. `v47.0` (prod) / `v46.1` (sandbox was tested on this) |
| `IntegrationSystemId` | The *Integration System ID*, which can differ from the display name |
| `DaysBack` | `0` = all history, otherwise only the last N days |
| `OnlyCompleted` | Keep only events with status `Completed` |
| `DownloadAllEventFiles` | See above |
| `VcfFolder` / `VcfExtension` | Sub-folder name and file extension for the VCF download |
| `OutputFolder` | Root output folder |

### Credentials (never commit these)

`Username` and `Password` are **not** stored in the repo. Keep them in .NET user secrets, which are stored in your
Windows profile (`%APPDATA%\Microsoft\UserSecrets\`), outside the repository:

```powershell
cd WorkdayEventDocuments
dotnet user-secrets set "Workday:Username" "your.name@yourcompany.com@tenant"
dotnet user-secrets set "Workday:Password" "your-password"
```

The username must be in Workday's `user@tenant` form. Environment variables also work
(`Workday__Username`, `Workday__Password`). OAuth is supported as an alternative via `Workday:ClientId`,
`Workday:ClientSecret` and `Workday:RefreshToken` (also kept in user secrets / environment variables).

Prefer a dedicated Integration System User (ISU) or an API Client for Integrations over a personal account.

## Run

```powershell
dotnet run
```

or press F5 in Visual Studio (profile `WorkdayEventDocuments`). The app takes no arguments and prints the absolute
output path.

## Security notes

- `.gitignore` excludes `bin/`, `obj/`, all `Downloads*/` folders and `*.log`. Downloaded files can contain financial
  or personal data and must stay on your machine.
- Do not paste credentials into chat tools, tickets or commits. If a password was ever shared, rotate it.
- Documents are downloaded over HTTPS only.
