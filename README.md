# SEQUFLOH

Job tracking for the shop floor: controlled work instructions, live task execution with timing and sign-off, and validation, FAI, repair and production tracking records, with dashboards and Excel/CSV/PDF reports.

## What's inside

| Folder | What it is |
| --- | --- |
| `src/Sequfloh.Api` | ASP.NET Core API (.NET 10). Also serves the built web app. |
| `web` | React web app (Vite, MUI). |
| `tests/Sequfloh.Tests` | Integration tests against a local PostgreSQL database. |
| `docs/DEPLOY.md` | Step-by-step guide to hosting on Neon, Render and Cloudflare R2. |

## Accounts and permissions

- The first visit shows a one-time setup page that creates the organization, the first site and the first administrator.
- Administrators add people under **Administration → People** and give each one or more roles, for all sites or a single site.
- Roles are plain sets of permissions. The starting roles (Operator, Technician, Supervisor, Quality, Engineering, Document control, Module administrator, Administrator) come with suggested permissions ticked, and any of them can be changed under **Administration → Roles and Permissions**.
- Every change and sign-in is written to the audit trail as who, what, when and a description.

## Running locally

Requirements: .NET 10 SDK, Node 22, PostgreSQL 15 or newer.

1. Create a database and put its connection string in `src/Sequfloh.Api/appsettings.Development.json` (`ConnectionStrings:Default`) or in the `DATABASE_URL` environment variable.
2. Start the API: `dotnet run --project src/Sequfloh.Api` (listens on http://localhost:5080 and applies migrations on start).
3. Start the web app: `npm install --prefix web` then `npm run dev --prefix web` and open http://localhost:5173.

Or run everything in Docker: `docker compose up --build` and open http://localhost:8080.

## Tests

`dotnet test` runs the integration tests. They create and drop their own database; point them at a local server with `SEQUFLOH_TEST_DB` (a Npgsql connection string for a role that can create databases).

## Configuration

| Variable | Purpose |
| --- | --- |
| `DATABASE_URL` | PostgreSQL connection, `postgres://user:password@host/db?sslmode=require` or a Npgsql string. |
| `JWT_SECRET` | Random string of at least 32 characters used to sign sessions. Changing it signs everyone out. |
| `STORAGE_PROVIDER` | `local` (default) or `s3`. |
| `STORAGE_LOCAL_PATH` | Folder for files when the provider is `local`. |
| `STORAGE_SERVICE_URL` | S3-compatible endpoint, e.g. `https://<account-id>.r2.cloudflarestorage.com`. |
| `STORAGE_BUCKET` | Bucket name. |
| `STORAGE_ACCESS_KEY` / `STORAGE_SECRET_KEY` | Access key pair for the bucket. |
| `PORT` | Port to listen on (set automatically by Render). |
| `Seed__AdminEmail` / `Seed__AdminPassword` | Optional: create an administrator on start instead of using the setup page. |
