# ThinkTech Storage API

A private document store for your own VPS. Your applications (BizOne ERP, ThinkTech ERP, Medidesk,
console apps, any .NET 10 or other web app) upload, download, delete and inspect documents over
HTTPS. Each application authenticates with its own **client id + client secret**.

```
 External Applications  (BizOne ERP · ThinkTech ERP · Medidesk · Console Apps)
          │
          │  HTTPS / REST   client id + secret  ->  bearer token  ->  file calls
          ▼
 ┌──────────────────────────── VPS ────────────────────────────┐
 │  nginx :443 (TLS, Let's Encrypt)                            │
 │     │                                                       │
 │     ▼                                                       │
 │  ThinkTech Storage API  (ASP.NET Core 10, 127.0.0.1:5080)   │
 │  Authentication · Upload · Download · Delete · Metadata     │
 │     │                                                       │
 │     ▼                                                       │
 │  /var/lib/thinktech-storage/{client-id}/...   (local disk)  │
 └─────────────────────────────────────────────────────────────┘
```

**Why the API runs on the VPS:** no application ever gets SSH or root access to the server. Apps only
hold an API client secret. You can scope that secret (read / write / delete) and revoke it on its own.
SSH stays an admin-only door.

---

## API

| Method | Route | Scope | Description |
|---|---|---|---|
| `POST` | `/api/v1/auth/token` | – (anonymous) | Exchange client id + secret for a bearer token (valid 60 min) |
| `POST` | `/api/v1/files` | `storage.write` | Upload (multipart: `file`, optional `category`, `referenceId`, `metadata` JSON) |
| `GET` | `/api/v1/files/{id}` | `storage.read` | Download (supports `Range` for resumable downloads, `ETag` = SHA-256) |
| `GET` | `/api/v1/files/{id}/metadata` | `storage.read` | File name, size, content type, SHA-256, category, reference, metadata, upload time |
| `DELETE` | `/api/v1/files/{id}` | `storage.delete` | Delete a file |
| `GET` | `/health` | – (anonymous) | `Healthy` when the storage folder is writable |

- **Isolation:** every client has its own folder. A client can never read or delete another client's files; it gets `404`.
- **Your app keeps the file id.** Upload returns an `id` (32 hex chars). Store it in your ERP database next to the invoice, patient, etc.
- Errors use RFC 7807 `application/problem+json`. Status codes: `401` bad/missing token, `403` missing scope, `404` not found, `413` too large, `415` file type not allowed.

### Authentication flow

```http
POST /api/v1/auth/token
Content-Type: application/json

{ "clientId": "bizone-erp", "clientSecret": "<secret>" }
```

```json
{ "access_token": "eyJhbGciOi...", "token_type": "Bearer", "expires_in": 3600, "scope": "storage.read storage.write storage.delete" }
```

Then send `Authorization: Bearer <access_token>` on every file call. The standard OAuth 2.0 form
(`grant_type=client_credentials&client_id=...&client_secret=...`) is accepted too, so OAuth client
libraries work out of the box.

Security details:
- Secrets are random 256-bit values. The server stores only a salted PBKDF2-SHA256 hash and compares in constant time.
- The token endpoint is rate-limited to 20 requests/minute per IP. **Cache the token** until it expires; don't fetch one per upload.
- Tokens are HMAC-SHA256 signed JWTs. If you disable or remove a client in the settings file, its tokens stop working immediately, with no restart.
- Content type is derived from the file extension, not trusted from the caller. Downloads are always `attachment` with `X-Content-Type-Options: nosniff`.

### Example: .NET 10 console app

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;

var http = new HttpClient { BaseAddress = new Uri("https://storage.example.com/") };

// 1. Authenticate (reuse the token for ~60 minutes)
var auth = await http.PostAsJsonAsync("api/v1/auth/token", new
{
    clientId = "bizone-erp",
    clientSecret = Environment.GetEnvironmentVariable("STORAGE_CLIENT_SECRET"),
});
auth.EnsureSuccessStatusCode();
var token = await auth.Content.ReadFromJsonAsync<TokenResponse>();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.access_token);

// 2. Upload
await using var source = File.OpenRead(@"C:\docs\invoice-1001.pdf");
using var form = new MultipartFormDataContent
{
    { new StreamContent(source), "file", "invoice-1001.pdf" },
    { new StringContent("invoices"), "category" },
    { new StringContent("INV-1001"), "referenceId" },
    { new StringContent("""{"customer":"ACME Ltd"}"""), "metadata" },
};
var upload = await http.PostAsync("api/v1/files", form);
upload.EnsureSuccessStatusCode();
var stored = await upload.Content.ReadFromJsonAsync<StoredFile>();
Console.WriteLine($"Stored as {stored!.Id}");          // save this id in your database

// 3. Download
await using (var download = await http.GetStreamAsync($"api/v1/files/{stored.Id}"))
await using (var target = File.Create(@"C:\temp\invoice-1001.pdf"))
{
    await download.CopyToAsync(target);
}

// 4. Delete
(await http.DeleteAsync($"api/v1/files/{stored.Id}")).EnsureSuccessStatusCode();

record TokenResponse(string access_token, string token_type, int expires_in, string scope);
record StoredFile(string Id, string FileName, string ContentType, long SizeBytes, string Sha256);
```

---

## Run locally

```bash
dotnet run --project src/ThinkTech.Storage.Api --launch-profile http
```

The API listens on `http://localhost:5059` and stores files in `src/ThinkTech.Storage.Api/App_Data/storage`
(git-ignored). `appsettings.Development.json` contains two **development-only** clients
(`dev-console` with full access, `dev-readonly` with read only). Their secrets are in
[`ThinkTech.Storage.Api.http`](src/ThinkTech.Storage.Api/ThinkTech.Storage.Api.http), which walks through
token → upload → metadata → download → delete. The development file is never published to the server.

## Managing clients

The API executable has built-in admin commands:

```bash
ThinkTech.Storage.Api generate-client bizone-erp     # prints a new secret + the hash for the settings file
ThinkTech.Storage.Api generate-signing-key           # prints a random Jwt:SigningKey
```

(Locally: `dotnet run --project src/ThinkTech.Storage.Api -- generate-client bizone-erp`.)

- Give the printed **secret** to the application, and keep it in that app's secret store, environment variable or vault. It is shown only once.
- Put the printed **hash** entry into the server settings file. Client ids are lowercase (`a-z 0-9 - _`, 3–64 characters).
- To **rotate** a secret: generate a new one for the same id, replace the `SecretHash`, then update the app.
- To **block** an app: set `"Enabled": false`. This takes effect immediately.

---

## Deploy to the VPS (Ubuntu / Debian)

Nothing in this setup needs the server's root password inside any application.

### 1. Publish (on your PC)

```bash
dotnet publish src/ThinkTech.Storage.Api -c Release -r linux-x64 --self-contained -o publish
```

Self-contained means no .NET runtime needs to be installed on the server.

### 2. One-time server setup

```bash
# service account with no login shell; folders for app, data and secrets
sudo useradd --system --no-create-home --shell /usr/sbin/nologin thinktech-storage
sudo mkdir -p /opt/thinktech-storage-api /var/lib/thinktech-storage /etc/thinktech-storage
sudo chown thinktech-storage:thinktech-storage /var/lib/thinktech-storage
sudo chmod 750 /var/lib/thinktech-storage
sudo apt update && sudo apt install -y nginx certbot python3-certbot-nginx
```

### 3. Copy the app

```bash
# from your PC
scp -r publish deploy <admin-user>@<vps-ip>:~/thinktech-storage/
```

```bash
# on the server
sudo cp -r ~/thinktech-storage/publish/* /opt/thinktech-storage-api/
sudo chmod +x /opt/thinktech-storage-api/ThinkTech.Storage.Api
```

### 4. Secrets and clients

```bash
sudo /opt/thinktech-storage-api/ThinkTech.Storage.Api generate-signing-key
sudo /opt/thinktech-storage-api/ThinkTech.Storage.Api generate-client bizone-erp
sudo /opt/thinktech-storage-api/ThinkTech.Storage.Api generate-client thinktech-erp
sudo /opt/thinktech-storage-api/ThinkTech.Storage.Api generate-client medidesk

sudo cp ~/thinktech-storage/deploy/settings.sample.json /etc/thinktech-storage/appsettings.json
sudo nano /etc/thinktech-storage/appsettings.json      # paste the key and hashes
sudo chown root:thinktech-storage /etc/thinktech-storage/appsettings.json
sudo chmod 640 /etc/thinktech-storage/appsettings.json
```

### 5. Service, reverse proxy, HTTPS, firewall

```bash
sudo cp ~/thinktech-storage/deploy/thinktech-storage-api.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now thinktech-storage-api
sudo journalctl -u thinktech-storage-api -n 50          # should say "Now listening on: http://127.0.0.1:5080"

sudo cp ~/thinktech-storage/deploy/nginx-thinktech-storage-api.conf /etc/nginx/sites-available/thinktech-storage-api
sudo nano /etc/nginx/sites-available/thinktech-storage-api   # set server_name to your domain
sudo ln -s /etc/nginx/sites-available/thinktech-storage-api /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo certbot --nginx -d storage.example.com              # free TLS certificate + auto-renewal

sudo ufw allow OpenSSH && sudo ufw allow 'Nginx Full' && sudo ufw enable
curl https://storage.example.com/health                  # -> Healthy
```

You need a (sub)domain, e.g. `storage.yourcompany.com`, with an A record pointing at the VPS IP. Let's
Encrypt needs it to issue the certificate. Don't send client secrets over plain HTTP to a bare IP.

### Updating

```bash
dotnet publish src/ThinkTech.Storage.Api -c Release -r linux-x64 --self-contained -o publish
scp -r publish <admin-user>@<vps-ip>:~/thinktech-storage/
ssh <admin-user>@<vps-ip> "sudo cp -r ~/thinktech-storage/publish/* /opt/thinktech-storage-api/ && sudo systemctl restart thinktech-storage-api"
```

Settings and stored files are outside `/opt/thinktech-storage-api`, so redeploys never touch them.

### Server hardening checklist

- [ ] Create a personal admin user with `sudo`, log in with an **SSH key**, then set `PermitRootLogin no` and `PasswordAuthentication no` in `/etc/ssh/sshd_config`.
- [ ] If the root password was ever shared (chat, email, docs), change it.
- [ ] `sudo apt install fail2ban unattended-upgrades`.
- [ ] Only ports 22, 80 and 443 are open (`ufw status`). Port 5080 is bound to localhost only.
- [ ] Back up `/var/lib/thinktech-storage` and `/etc/thinktech-storage` off the server (e.g. `restic` or `rsync` nightly). Each file's `.json` sidecar carries its metadata, so a plain file copy is a complete backup.
- [ ] Monitor disk space and `GET /health`.

---

## Configuration

| Key | Default | Notes |
|---|---|---|
| `Jwt:SigningKey` | – (required) | ≥ 32 chars, from `generate-signing-key` |
| `Jwt:AccessTokenMinutes` | `60` | Token lifetime |
| `Jwt:Issuer` / `Jwt:Audience` | `thinktech-storage-api` / `thinktech-storage-clients` | |
| `Storage:RootPath` | `/var/lib/thinktech-storage` | Relative paths resolve from the app folder |
| `Storage:MaxFileSizeBytes` | `104857600` (100 MB) | Keep nginx `client_max_body_size` above this |
| `Storage:AllowedExtensions` | office, PDF, images, xml/json, zip/7z, dcm | Empty list = allow all |
| `StorageClients:Clients` | `[]` | `ClientId`, `DisplayName`, `SecretHash`, `Scopes`, `Enabled` |

On the server, values come from `appsettings.json` in the app folder, overridden by the file in
`THINKTECH_STORAGE_SETTINGS`, overridden by environment variables (e.g. `Jwt__SigningKey`).
The API validates all of it at startup and refuses to run with a missing key or a malformed client.

> Note: .NET merges JSON arrays by index. If you override `AllowedExtensions` in the server file,
> list at least as many entries as the default, or set the full list there.

### Storage layout

```
/var/lib/thinktech-storage/
  bizone-erp/
    3f/a1/3fa1c0...e9.bin     <- file content (name on disk is the server-generated id)
    3f/a1/3fa1c0...e9.json    <- metadata: original name, size, SHA-256, category, reference, ...
  medidesk/
    ...
```

Names on disk are never taken from user input, so path traversal is impossible. The two levels of shard
folders keep directories small even with millions of files.

## Project structure

```
src/ThinkTech.Storage.Api/
  Program.cs                      composition root: config, auth, limits, pipeline
  Authentication/                 secret hashing, credential check, token issuing, scopes
  Configuration/                  strongly typed + validated options
  Contracts/                      request/response DTOs
  Controllers/                    AuthController (token), FilesController (upload/download/delete/metadata)
  Storage/                        IFileStorage, LocalFileStorage, health check, upload validation
  Tools/                          generate-client / generate-signing-key admin commands
deploy/                           systemd unit, nginx site, server settings template
```

`IFileStorage` is the only storage dependency. To move files to S3/MinIO/Azure Blob or another server
later, add another implementation and register it in `Program.cs`. No API or client changes needed.
