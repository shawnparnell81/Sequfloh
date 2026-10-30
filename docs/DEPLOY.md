# Putting SEQUFLOH online (free)

This guide puts SEQUFLOH on three free services:

- **Neon** holds the database.
- **Cloudflare R2** holds uploaded files and pictures.
- **Render** runs the app itself.

Plan on about 45 minutes. You need a GitHub account with this project pushed to a private repository, and a web browser. Nothing here costs money, but Cloudflare asks for a card on file before it turns R2 on.

> **Before you start:** check with your company that keeping job data on free outside services is allowed. Free tiers have no uptime promise.

Keep a plain text file open while you work. You will collect five values in it:

- the database address
- the R2 address
- the bucket name
- the access key
- the secret key

Delete the text file when you are done.

---

## Part 1: Database (Neon)

1. Go to https://neon.tech and sign up. Signing in with GitHub is fine.
2. Click **New Project**.
3. Name it `sequfloh`.
4. Pick the region closest to you, for example **AWS US East (Ohio)**.
5. Leave the Postgres version at the default and click **Create project**.
6. On the project dashboard, click **Connect**.
7. Make sure **Connection pooling** is switched **off**. The app needs a direct connection to run its database updates.
8. Copy the connection string. It starts with `postgresql://` and ends with `sslmode=require`. Paste it into your text file as **DATABASE_URL**.

## Part 2: File storage (Cloudflare R2)

1. Go to https://dash.cloudflare.com and sign up.
2. In the left menu, click **R2 Object Storage**.
3. Follow the prompts to turn on R2. This is where Cloudflare asks for a card. The free allowance is 10 GB.
4. Click **Create bucket**.
5. Name it `sequfloh-files`, leave the location on **Automatic**, and click **Create bucket**.
6. Paste `sequfloh-files` into your text file as **STORAGE_BUCKET**.
7. Go back to the R2 overview page.
8. On the right, find **Account ID**. Copy it, then write `https://<the account id>.r2.cloudflarestorage.com` into your text file as **STORAGE_SERVICE_URL**.
9. Click **Manage R2 API Tokens**, then **Create API token**.
10. Name the token `sequfloh`.
11. Choose the **Object Read & Write** permission and limit it to the `sequfloh-files` bucket.
12. Click **Create API Token**.
13. Copy the **Access Key ID** into your text file as **STORAGE_ACCESS_KEY**.
14. Copy the **Secret Access Key** into your text file as **STORAGE_SECRET_KEY**. Cloudflare shows the secret only once.

Leave the bucket private. Do not turn on public access, because the app hands out files itself, only to people who are signed in.

## Part 3: The app (Render)

1. Go to https://render.com and sign up with GitHub.
2. Click **New**, then **Blueprint**.
3. Connect your GitHub account and pick the SEQUFLOH repository. Render reads `render.yaml` from the project and shows one web service called **sequfloh** on the **Free** plan.
4. Render asks for the values marked as secret. Paste each one from your text file:
   - **DATABASE_URL**
   - **STORAGE_SERVICE_URL**
   - **STORAGE_BUCKET**
   - **STORAGE_ACCESS_KEY**
   - **STORAGE_SECRET_KEY**
5. Leave the rest alone. Render generates **JWT_SECRET** by itself, and **STORAGE_PROVIDER** is already set to `s3`.
6. Click **Apply**. The first build takes about 5 to 10 minutes. Watch the **Logs** tab until it says `Application started`.
7. Open the address Render shows at the top. It looks like `https://sequfloh-xxxx.onrender.com`.
8. The **Set up SEQUFLOH** page appears. Enter:
   - your organization name
   - your first site
   - your time zone
   - your name, email and a password of at least 10 characters
9. Click **Create organization and sign in**. This page never shows again.

## Part 4: Add your coworkers

1. In the menu on the left, under **Administration**, click **People**.
2. Click **Add person**.
3. Enter their name, their email and a temporary password.
4. Pick a first role, for example **Operator** or **Supervisor**.
5. Tell them the temporary password in person. They choose their own the first time they sign in.
6. To change what a role can do, open **Roles and Permissions**, click **Edit** on the role, tick or untick boxes, and click **Save role**.

## Good to know

- **Slow first load.** On the free plan, Render puts the app to sleep after 15 minutes with no visitors. The next visit takes about a minute to wake it, and the page shows a loading spinner while it does. After that it is quick.
- **Files live in R2, not on Render.** The free Render plan wipes its own disk on every restart, which is why pictures and attachments go to R2.
- **Neon also sleeps.** It wakes in a second or two. The app retries the connection on its own.
- **Backups.** Neon keeps a short restore history on the free plan. For your own copy, use Neon's **Backup & Restore** page, or run `pg_dump` against the DATABASE_URL.
- **Updates.** Every push to the main branch of the repository rebuilds and redeploys the app. Database changes are applied automatically when it starts.

## Pushing the project to GitHub from Windows (PowerShell)

These commands assume the project is in `C:\sequfloh` and that you made an empty **private** repository on GitHub named `sequfloh`. Replace `YOUR-NAME` with your GitHub user name. Run each command on its own, one at a time.

```powershell
git -C C:\sequfloh remote add origin https://github.com/YOUR-NAME/sequfloh.git
```

```powershell
git -C C:\sequfloh branch -M main
```

```powershell
git -C C:\sequfloh push -u origin main
```

## Trying it on your own PC first (optional)

This needs Docker Desktop installed and running. The first command builds and starts the app with its own local database. Then open http://localhost:8080 in your browser.

```powershell
docker compose -f C:\sequfloh\docker-compose.yml up --build -d
```

The second command stops it again.

```powershell
docker compose -f C:\sequfloh\docker-compose.yml down
```

## Nightly database backup (Render cron job)

Neon's free plan only keeps 6 hours of restore history, so a Render cron job named `sequfloh-db-backup` saves a full copy of the database to the Cloudflare R2 bucket every night. It runs at 07:00 UTC, which is 3:00 AM New York in summer (EDT) and 2:00 AM in winter (EST). Copies are saved under `backups/db/` as `sequfloh-db-YYYY-MM-DD-HHMM.dump`. Copies older than 30 days are deleted automatically. Uploaded files already live in R2 and are not part of this copy. The app never reads or serves anything under `backups/`.

The job is set up in `render.yaml` and uses the files in the `backup` folder.

### Creating the cron job

Use one of these two ways.

- **Blueprint (if the app was set up from `render.yaml`):** in Render, open **Blueprints**, pick the Sequfloh blueprint, and click **Manual sync** (or wait for the next sync). Review the new `sequfloh-db-backup` cron job and apply it. Its five settings are copied from the `sequfloh` web service automatically.
- **By hand:** click **New > Cron Job** and connect the Sequfloh repository on the `main` branch. Set the language to **Docker**, the Dockerfile path to `./backup/Dockerfile`, the Docker build context to `./backup`, and the schedule to `0 7 * * *`. Pick the Starter instance type.

### Its settings

Open the `sequfloh` web service, go to **Environment**, and copy these five values into the cron job's **Environment** page with the same names:

- `DATABASE_URL`
- `STORAGE_SERVICE_URL`
- `STORAGE_BUCKET`
- `STORAGE_ACCESS_KEY`
- `STORAGE_SECRET_KEY`

If you used the Blueprint, check that the five are there. If any is missing, the run stops and its log names the missing ones. The values are never printed in the log.

### Running it once by hand

Open the `sequfloh-db-backup` cron job and click **Trigger Run**. Open **Logs**. A good run ends with `Uploaded backups/db/sequfloh-db-...dump` and `Backup finished`.

### Where backups are

In Cloudflare, go to **R2**, open the bucket the app uses, then open the `backups/db/` folder. Click a file and choose **Download** to save it to your PC.

### Restoring a backup

You need the PostgreSQL 17 or 18 tools (`pg_restore`) on your PC.

Into a new Neon branch, to check it or to recover data:

1. In Neon, create a new branch. Under **Databases**, add a new empty database to it (for example `restore`), and copy its connection string.
2. Run this, using your connection string and file name:

```
pg_restore --no-owner --no-privileges --dbname="postgresql://USER:PASSWORD@HOST/restore?sslmode=require" sequfloh-db-YYYY-MM-DD-HHMM.dump
```

Into a local Postgres (for example the one from `docker compose`):

```
createdb -h localhost -p 5432 -U sequfloh sequfloh_restore
pg_restore --no-owner --no-privileges -h localhost -p 5432 -U sequfloh -d sequfloh_restore sequfloh-db-YYYY-MM-DD-HHMM.dump
```

Only point the web service's `DATABASE_URL` at a restored copy on purpose: that replaces the data the app is using.
