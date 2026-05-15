# MongoDelta — Blazor Test Plan

Test app: `samples/BlazorMongoDeltaTest`

```bash
# Start MongoDB replica set
./dev-server.sh

# Run the app (from samples/BlazorMongoDeltaTest)
dotnet run
# → http://localhost:5001
```

---

## DevTools setup (do this once)

1. Open browser DevTools (`F12`)
2. Go to the **Network** tab
3. Make sure **Disable cache** is **OFF** — if it's on, the browser skips `If-None-Match` entirely and you'll never see a 304
4. Filter by **Fetch/XHR** for the API page tests, or leave unfiltered for the SSR page tests
5. Keep the Network tab open throughout — it persists across navigations

### What to look for

| Header | Tab | Where |
|---|---|---|
| `ETag` | Response Headers | Set by MongoDelta on every 200 |
| `Cache-Control: no-cache` | Response Headers | Tells the browser to always revalidate |
| `If-None-Match` | Request Headers | Browser sends this on repeat requests |
| Status `304` | Status column | 0 B transferred, no body |

---

## Seed test data first

Navigate to **Products (API)** → click **Seed / Reset data**.
You should see 4 products appear and the status badge show `HTTP 200 OK`.

---

## T1 — First request returns 200 with ETag

**Page:** Products (API) → `/products-api`

**Steps:**
1. Clear the Network tab (🚫 icon)
2. Hard-refresh the page (`Ctrl+Shift+R`)
3. In the Network tab find the `api/products` request

**What to check:**
- Status: `200`
- Response Headers → `ETag` is present (format: `"<token>-<timestamp>"`)
- Response Headers → `Cache-Control: no-cache`
- The status badge on the page shows `HTTP 200 OK`

---

## T2 — Repeat request returns 304 (cache hit)

**Page:** Products (API) → `/products-api`

**Steps:**
1. Click **Reload** on the page (don't hard-refresh — that bypasses ETags)
2. Find the new `api/products` request in the Network tab

**What to check:**
- Status: `304`
- Request Headers → `If-None-Match` contains the ETag from T1
- Response body: `0 B` (empty — no product list transferred)
- The product list on the page is still showing (kept from previous load)
- Status badge on page: `HTTP 304 Not Modified`

---

## T3 — Write invalidates the cache

**Page:** Products (API) → `/products-api`

**Steps:**
1. Click **Add random product**
2. Watch the Network tab

**What to check:**
- The `POST /api/products` request has **no ETag** (MongoDelta skips non-GET)
- The immediately following `GET /api/products` returns `200` (not 304)
- The ETag value in the 200 response is **different** from T1's ETag
- The new product appears in the list

**Why:** The POST advanced `$clusterTime` on the replica set. The next GET's ETag
no longer matches the browser's stored one, so the server returns fresh data.

---

## T4 — POST has no ETag

**Page:** Products (API) → `/products-api`

**Steps:**
1. Click **Seed / Reset data**
2. In the Network tab, click the `api/seed` POST request

**What to check:**
- Response Headers: **no `ETag` header**
- Response Headers: **no `Cache-Control` from MongoDelta**
- Status: `200` (whatever the handler returns)

---

## T5 — Blazor SSR page routes are not cached

**Steps:**
1. In the Network tab, unfilter (show all requests including `Doc`)
2. Navigate to `/products-api` (the full page load, not just the fetch)
3. Click the HTML document request at the top of the Network tab

**What to check:**
- Response Headers: **no `ETag`** header from MongoDelta
- Response Headers: **no `Cache-Control: no-cache`** from MongoDelta
- Status: `200`

**Why:** `OnSkip` is configured to skip any path that doesn't start with `/api`.
The Blazor page route `/products-api` is the HTML shell, not the API call.

---

## T6 — Static SSR + direct DB: DB call skipped on 304

**Page:** Products (SSR) → `/products-ssr`

This page injects `IMongoCollection<Product>` directly — no API call. Watch the
**server console** (terminal where `dotnet run` is running), not the browser.

**Steps:**
1. Navigate to `/products-ssr`
2. Check terminal — you should see:
   ```
   >>> DB call executing (OnInitializedAsync)
   ```
3. Navigate away to Home, then back to `/products-ssr`
4. Check the terminal again

**What to check:**
- First visit: `DB call executing` appears in the terminal
- Second visit (if within same browser session, data unchanged): **no** `DB call executing` in the terminal
- The product list still renders correctly on the second visit

**Why:** MongoDelta fires at the middleware layer before the Blazor component runs.
On a 304 the entire component lifecycle is skipped — `OnInitializedAsync` never
executes and MongoDB is never queried.

> **Note:** In Blazor's Static SSR mode, navigating between pages does a full
> server round-trip (unless enhanced navigation is active). Each navigation is a
> fresh HTTP GET, so the browser's ETag handling applies on every navigation.

---

## T7 — Interactive Server: DB call runs on every button click

**Page:** Products (Interactive) → `/products-interactive`

**Steps:**
1. Navigate to `/products-interactive`
2. Check the terminal — `DB call #1 via OnInitializedAsync` should appear
3. Click **Refresh from DB** several times
4. Watch the **DB call count** badge on the page and the terminal

**What to check:**
- Each button click increments the counter and logs to the terminal:
  ```
  >>> DB call #2 via button click (outside MongoDelta)
  >>> DB call #3 via button click (outside MongoDelta)
  ```
- In the Network tab: no `api/` requests appear on button click (it's SignalR, not HTTP)
- In the Network tab: you can see an open WebSocket connection (`_blazor?...`)

**Why:** After the initial HTTP prerender, Interactive Server components communicate
via SignalR. Button clicks go over that WebSocket — MongoDelta is not involved.
Each DB call is unconditional regardless of whether data has changed.

---

## T8 — App restart invalidates all client ETags

**Steps:**
1. Visit `/products-api` and confirm a 304 on **Reload**
2. Stop the app (`Ctrl+C` in the terminal)
3. Run `dotnet run` again (this recompiles, changing the assembly write time)
4. Without clearing the browser cache, click **Reload** on `/products-api`

**What to check:**
- Status: `200` (not 304)
- The ETag value has a different prefix (the deployment token changed)
- All subsequent Reloads go back to 304 until the next restart

**Why:** The ETag includes the assembly's last-write timestamp. A recompile changes
this, making every stored client ETag stale.

---

## T9 — Confirm `$clusterTime` is the source

**Steps:**
1. Visit `/products-api`, click **Reload** until you have a stable 304
2. In the browser, copy the `ETag` value from the Response Headers panel
3. Open mongosh and run:
   ```js
   db.runCommand({hello:1}).$clusterTime.clusterTime
   ```
4. The timestamp portion of the ETag (the `seconds.ordinal` part) should match
   the `$clusterTime` returned by the `hello` command

**What to check:**
- ETag format: `"{assemblyToken}-{seconds}.{ordinal}"`
- The `{seconds}` value matches the `Timestamp` field of `$clusterTime`

---

## Common issues

| Symptom | Cause | Fix |
|---|---|---|
| Always getting 200, never 304 | DevTools **Disable cache** is ON | Turn it off |
| Always getting 200, never 304 | Hard-refreshing (`Ctrl+Shift+R`) | Use soft reload or the page's Reload button |
| No `ETag` on API responses | MongoDelta not registered, or `OnSkip` too broad | Check `Program.cs` |
| `ETag` appearing on Blazor page HTML | `OnSkip` missing or wrong path | Ensure `OnSkip` skips non-`/api` paths |
| DB call still logged on 304 (SSR page) | Browser cache disabled or hard-refresh used | Ensure Disable cache is OFF, use soft reload |
| Interactive button clicks show in Network tab | Expected — they're HTTP fetch calls | Use WS filter to see SignalR traffic instead |
