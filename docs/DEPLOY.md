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
