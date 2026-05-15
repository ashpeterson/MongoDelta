# MongoDelta — Blazor Web App Integration Test Plan

Target: **Blazor Web App (.NET 10, Static SSR default)**  
MongoDelta: local project reference (clone the repo alongside the test app)

---

## Prerequisites

```bash
dotnet --version          # must be 10.x
mongod --version          # or use Atlas free tier (M0)
```

If running locally without Atlas, start a single-node replica set — the dev server
script from the MongoDelta repo works:

```bash
./dev-server.sh           # starts mongod on port 27117
# or point MONGO_URI at Atlas
```

---

## 1. Create the test app

```bash
dotnet new blazor -n BlazorMongoDeltaTest -o BlazorMongoDeltaTest
cd BlazorMongoDeltaTest
```

Add dependencies:

```bash
# MongoDB driver
dotnet add package MongoDB.Driver --version 3.4.0

# MongoDelta — reference the local clone (no NuGet package yet)
dotnet add reference ../MongoDelta/src/MongoDelta/MongoDelta.csproj
```

> If you cloned MongoDelta somewhere else, adjust the path.  
> Once MongoDelta is on NuGet, replace with `dotnet add package MongoDelta`.

---

## 2. Data model

Create `Models/Product.cs`:

```csharp
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace BlazorMongoDeltaTest.Models;

public class Product
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public string Name    { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price  { get; set; }
}
```

---

## 3. Wire up MongoDB + MongoDelta in Program.cs

Replace the generated `Program.cs` with:

```csharp
using BlazorMongoDeltaTest.Components;
using BlazorMongoDeltaTest.Models;
using MongoDelta;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents();

// Register MongoDB
var mongoUri = builder.Configuration["Mongo:Uri"]
               ?? "mongodb://127.0.0.1:27117/?directConnection=true&replicaSet=singleNodeReplSet";
var mongoClient = new MongoClient(mongoUri);
builder.Services.AddSingleton<IMongoClient>(mongoClient);
builder.Services.AddSingleton(mongoClient
    .GetDatabase("blazor_test")
    .GetCollection<Product>("products"));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

// ── MongoDelta ────────────────────────────────────────────────────────────────
// Skip Blazor SSR routes — only cache the /api/* endpoints below.
app.UseMongoDelta(mongoClient, options =>
{
    options.OnSkip = ctx => !ctx.Request.Path.StartsWithSegments("/api");
});

// ── API endpoints (GET — MongoDelta will cache these) ─────────────────────────
app.MapGet("/api/products", async (IMongoCollection<Product> col) =>
    await col.Find(_ => true).ToListAsync());

app.MapGet("/api/products/{id}", async (string id, IMongoCollection<Product> col) =>
{
    var product = await col.Find(p => p.Id == id).FirstOrDefaultAsync();
    return product is null ? Results.NotFound() : Results.Ok(product);
});

// ── Write endpoints (POST/PUT/DELETE — NOT cached, MongoDelta skips non-GET) ──
app.MapPost("/api/products", async (Product product, IMongoCollection<Product> col) =>
{
    await col.InsertOneAsync(product);
    return Results.Created($"/api/products/{product.Id}", product);
});

app.MapPut("/api/products/{id}", async (string id, Product updated,
    IMongoCollection<Product> col) =>
{
    updated.Id = id;
    await col.ReplaceOneAsync(p => p.Id == id, updated);
    return Results.Ok(updated);
});

app.MapDelete("/api/products/{id}", async (string id, IMongoCollection<Product> col) =>
{
    await col.DeleteOneAsync(p => p.Id == id);
    return Results.NoContent();
});

app.MapRazorComponents<App>();

app.Run();
```

Add `appsettings.Development.json` override (or set env var):

```json
{
  "Mongo": {
    "Uri": "mongodb://127.0.0.1:27117/?directConnection=true&replicaSet=singleNodeReplSet"
  }
}
```

---

## 4. Seed endpoint (dev only)

Add this after the DELETE endpoint for easy test setup:

```csharp
if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/seed", async (IMongoCollection<Product> col) =>
    {
        await col.DeleteManyAsync(_ => true);
        await col.InsertManyAsync([
            new Product { Name = "Widget A", Category = "Widgets", Price = 9.99m },
            new Product { Name = "Widget B", Category = "Widgets", Price = 14.99m },
            new Product { Name = "Gadget X", Category = "Gadgets", Price = 49.99m },
        ]);
        return Results.Ok("Seeded 3 products");
    });
}
```

---

## 5. Blazor component

Replace `Components/Pages/Home.razor` with:

```razor
@page "/"
@inject IHttpClientFactory HttpFactory

<PageTitle>Products</PageTitle>

<h1>Products</h1>

@if (_loading)
{
    <p>Loading…</p>
}
else if (_products.Count == 0)
{
    <p>No products. <button @onclick="Seed">Seed test data</button></p>
}
else
{
    <table>
        <thead><tr><th>Name</th><th>Category</th><th>Price</th></tr></thead>
        <tbody>
            @foreach (var p in _products)
            {
                <tr>
                    <td>@p.Name</td>
                    <td>@p.Category</td>
                    <td>@p.Price.ToString("C")</td>
                </tr>
            }
        </tbody>
    </table>
}

<hr/>
<button @onclick="Load">Reload from API</button>
<button @onclick="Seed">Re-seed</button>
<p>Last fetch status: <strong>@_lastStatus</strong></p>

@code {
    private List<Product> _products = [];
    private bool _loading = true;
    private string _lastStatus = "—";

    protected override async Task OnInitializedAsync() => await Load();

    private async Task Load()
    {
        _loading = true;
        var http = HttpFactory.CreateClient("api");
        var resp = await http.GetAsync("/api/products");
        _lastStatus = $"{(int)resp.StatusCode} {resp.StatusCode}  " +
                      $"ETag: {resp.Headers.ETag}";
        if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            // 304 — keep existing list
        }
        else
        {
            _products = await resp.Content.ReadFromJsonAsync<List<Product>>() ?? [];
        }
        _loading = false;
    }

    private async Task Seed()
    {
        var http = HttpFactory.CreateClient("api");
        await http.PostAsync("/api/seed", null);
        await Load();
    }
}
```

Add `Models/Product.cs` reference to `_Imports.razor`:

```razor
@using BlazorMongoDeltaTest.Models
```

Register `HttpClient` in `Program.cs` (add before `var app = builder.Build()`):

```csharp
builder.Services.AddHttpClient("api", client =>
    client.BaseAddress = new Uri("https://localhost:5001/"));
```

> Adjust the port to match your `launchSettings.json`.

---

## 6. Run the app

```bash
dotnet run
```

Open `https://localhost:5001` (or whatever port is shown).  
Open **DevTools → Network tab**, check **Disable cache** OFF, filter by `XHR/Fetch`.

---

## Test Scenarios

### T1 — First request: 200 + ETag set

**Steps:**
1. Navigate to `https://localhost:5001`
2. In DevTools Network tab, find the `GET /api/products` request

**Expected:**
- Status: `200 OK`
- Response headers contain `ETag: "…"` (format: `"{deployToken}-{timestamp}"`)
- Response headers contain `Cache-Control: no-cache`
- Response body contains the product list (or empty array)

**Pass criteria:** ETag header present, Cache-Control is `no-cache`.

---

### T2 — Repeat request, no writes: 304

**Steps:**
1. Click **Reload from API** in the page UI (or press the button a second time)
2. Observe the `/api/products` request in Network tab

**Expected:**
- Status: `304 Not Modified`
- Request headers show `If-None-Match: "…"` (browser sent the stored ETag back)
- Response body is empty (0 bytes)
- The product list in the UI is unchanged

**Pass criteria:** 304, no body, UI still displays products.

---

### T3 — Write invalidates cache

**Steps:**
1. Send a POST to add a product (use curl or the Seed button):
   ```bash
   curl -X POST https://localhost:5001/api/products \
     -H "Content-Type: application/json" \
     -d '{"name":"New Item","category":"Test","price":1.00}'
   ```
2. Click **Reload from API**

**Expected:**
- Status: `200 OK` (not 304)
- ETag value is different from the one seen in T1
- New product appears in the UI

**Why:** The write advanced `$clusterTime`. The ETag the browser sent (`If-None-Match`) no longer matches, so the server returns a fresh 200.

**Pass criteria:** 200 with a new ETag, new product visible.

---

### T4 — POST is never cached

**Steps:**
1. In DevTools, observe the `POST /api/seed` request

**Expected:**
- No `ETag` header on the POST response
- No `Cache-Control` header set by MongoDelta
- Status is whatever the handler returns (200/201)

**Why:** MongoDelta only activates on GET and HEAD. POST passes through untouched.

**Pass criteria:** No ETag header on POST responses.

---

### T5 — Blazor SSR routes are not affected

**Steps:**
1. Navigate to `https://localhost:5001` (the root Blazor page)
2. In DevTools, find the initial HTML page request (document type)

**Expected:**
- No `ETag` header on the Blazor page response
- No `Cache-Control: no-cache` from MongoDelta

**Why:** `OnSkip` returns `true` for any path that doesn't start with `/api`.

**Pass criteria:** Blazor page responses are unmodified by MongoDelta.

---

### T6 — Single item endpoint caching

**Steps:**
1. Note the `Id` of a product (from the `/api/products` response body)
2. In the browser address bar or curl, fetch `/api/products/{id}` twice

**Expected:**
- First fetch: `200` with ETag
- Second fetch: `304`, no body

**Pass criteria:** Same ETag behaviour as T1/T2 but scoped to a single item.

---

### T7 — Redeploy invalidates all client caches

**Steps:**
1. Stop the app
2. Restart it (`dotnet run`)
3. Without clearing browser cache, reload the page and click **Reload from API**

**Expected:**
- `200 OK` (not 304)
- New ETag with a different deployment token (assembly write time changed on rebuild)

**Why:** `assemblyWriteTime` is baked into the ETag. A new build changes the token, so all stored client ETags are immediately stale.

**Pass criteria:** First request after restart returns 200 regardless of stored ETag.

> To simulate container deployments where assembly time is unreliable, set:
> ```csharp
> options.DeploymentVersion = "v1.2.3";  // pin to your build tag
> ```

---

### T8 — Per-user caching with Suffix

**Steps:**
1. Add authentication middleware and update `UseMongoDelta`:
   ```csharp
   app.UseMongoDelta(mongoClient, options =>
   {
       options.OnSkip      = ctx => !ctx.Request.Path.StartsWithSegments("/api");
       options.Suffix      = ctx => ctx.User.Identity?.Name;
       options.VaryByHeaders = ["Authorization"];
   });
   ```
2. Log in as User A, fetch `/api/products` — note ETag
3. Log in as User B (different session), fetch `/api/products` — note ETag

**Expected:**
- User A and User B get different ETags (suffix appended differs)
- Response includes `Vary: Authorization` header
- User A's 304 is not served to User B

**Pass criteria:** ETags differ between users; Vary header present.

---

### T9 — OnSkip for health check route

**Steps:**
1. Add a health check endpoint and update skip predicate:
   ```csharp
   app.MapGet("/health", () => "ok");

   app.UseMongoDelta(mongoClient, options =>
   {
       options.OnSkip = ctx =>
           !ctx.Request.Path.StartsWithSegments("/api") ||
           ctx.Request.Path.StartsWithSegments("/health");
   });
   ```
2. Fetch `/health` multiple times

**Expected:**
- No ETag header on `/health` responses
- Always returns 200

**Pass criteria:** Health check bypasses middleware entirely.

---

### T10 — Standalone mongod fallback (optional)

Only run this if you have a standalone (non-replica-set) mongod available.

**Steps:**
1. Change the connection string to a standalone `mongod` (no `replicaSet` param)
2. Restart the app — check the log for:
   ```
   MongoDelta: standalone mongod detected — using version counter provider
   ```
3. Fetch `/api/products` → 200 + ETag
4. Fetch again without writing → 304 (counter unchanged)
5. Call `await store!.IncrementAsync()` after a write → next fetch is 200

**Note:** In the `Program.cs` setup above, this requires capturing the store:
```csharp
var (_, store) = app.UseMongoDelta(mongoClient, options => { ... });
// Inject `store` into a service if you need to call IncrementAsync from handlers
```

**Pass criteria:** Logs confirm standalone mode; 304 behaviour works correctly via counter.

---

## Observing ETags in DevTools

1. Open **DevTools → Network** (`F12`)
2. Check that **Disable cache** is **OFF** (important — if ticked, the browser won't send `If-None-Match`)
3. Filter by `Fetch/XHR`
4. Click a request → **Headers tab**

Look for:

| Header | Where | Example value |
|---|---|---|
| `ETag` | Response | `"638800000000000-1748000042.1"` |
| `Cache-Control` | Response | `no-cache` |
| `If-None-Match` | Request (repeat only) | `"638800000000000-1748000042.1"` |
| `Vary` | Response (if Suffix set) | `Authorization` |

A `304` response will show **0 B** transferred and no response body.

---

## Common issues

| Symptom | Likely cause | Fix |
|---|---|---|
| Never getting 304 | DevTools **Disable cache** is ON | Turn it off |
| Never getting 304 | Browser not sending `If-None-Match` | Check the request headers panel |
| 304 after every write | Standalone mode, `IncrementAsync` not called | Call store after each write |
| ETag present on Blazor page responses | `OnSkip` not configured | Add `OnSkip` as shown in step 3 |
| `$clusterTime` not found | Connected to standalone mongod | Check connection string includes `replicaSet=` |
| ETag changes on every restart | Container with volatile filesystem | Set `options.DeploymentVersion` to a fixed build tag |
