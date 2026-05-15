using BlazorMongoDeltaTest.Components;
using BlazorMongoDeltaTest.Models;
using MongoDelta;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// MongoDB
var mongoUri = builder.Configuration["Mongo:Uri"]
    ?? "mongodb://127.0.0.1:27117/?directConnection=true&replicaSet=singleNodeReplSet";
var mongoClient = new MongoClient(mongoUri);
builder.Services.AddSingleton<IMongoClient>(mongoClient);
builder.Services.AddSingleton(
    mongoClient.GetDatabase("blazor_delta_test").GetCollection<Product>("products"));

// HttpClient for components that call the API endpoints
builder.Services.AddHttpClient("api", client =>
    client.BaseAddress = new Uri("http://localhost:5001/"));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

// MongoDelta — only cache /api/* routes, leave Blazor SSR routes untouched
app.UseMongoDelta(mongoClient, options =>
{
    options.OnSkip = ctx => !ctx.Request.Path.StartsWithSegments("/api");
});

// ── API endpoints ────────────────────────────────────────────────────────────
app.MapGet("/api/products", async (IMongoCollection<Product> col) =>
    await col.Find(_ => true).ToListAsync());

app.MapGet("/api/products/{id}", async (string id, IMongoCollection<Product> col) =>
{
    var p = await col.Find(x => x.Id == id).FirstOrDefaultAsync();
    return p is null ? Results.NotFound() : Results.Ok(p);
});

app.MapPost("/api/products", async (Product product, IMongoCollection<Product> col) =>
{
    await col.InsertOneAsync(product);
    return Results.Created($"/api/products/{product.Id}", product);
});

app.MapDelete("/api/products/{id}", async (string id, IMongoCollection<Product> col) =>
{
    await col.DeleteOneAsync(p => p.Id == id);
    return Results.NoContent();
});

// Dev-only seed endpoint
if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/seed", async (IMongoCollection<Product> col) =>
    {
        await col.DeleteManyAsync(_ => true);
        await col.InsertManyAsync([
            new Product { Name = "Widget A",  Category = "Widgets", Price = 9.99m  },
            new Product { Name = "Widget B",  Category = "Widgets", Price = 14.99m },
            new Product { Name = "Gadget X",  Category = "Gadgets", Price = 49.99m },
            new Product { Name = "Gadget Pro", Category = "Gadgets", Price = 89.99m },
        ]);
        return Results.Ok("Seeded 4 products");
    });
}

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
