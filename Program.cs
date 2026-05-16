using Microsoft.AspNetCore.Http.Features;
using System.IO.Compression;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 500_000_000;
});

var app = builder.Build();

var root = Path.Combine(Directory.GetCurrentDirectory(), "data/sites");
Directory.CreateDirectory(root);

// =========================
// STATIC FILES (UI)
// =========================
app.UseDefaultFiles();
app.UseStaticFiles();

// =========================
// HELPERS
// =========================
string Slug(string input)
{
    input = (input ?? "").ToLower().Trim();
    return new string(input.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
}

// =========================
// API: LIST SITES
// =========================
app.MapGet("/api/sites", () =>
{
    return Directory.GetDirectories(root)
        .Select(Path.GetFileName)
        .ToList();
});

// =========================
// API: LIST HTML FILES
// =========================
app.MapGet("/api/files/{site}", (string site) =>
{
    var dir = Path.Combine(root, site);

    if (!Directory.Exists(dir))
        return Results.NotFound();

    var files = Directory.GetFiles(dir, "*.html", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(dir, f))
        .ToList();

    return Results.Ok(files);
});

// =========================
// API: UPLOAD SITE
// =========================
app.MapPost("/api/upload", async (HttpRequest req) =>
{
    var form = await req.ReadFormAsync();

    var nameRaw = form["name"].ToString();
    var name = Slug(nameRaw);

    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { ok = false, error = "Missing site name" });

    var siteDir = Path.Combine(root, name);

    if (Directory.Exists(siteDir))
        Directory.Delete(siteDir, true);

    Directory.CreateDirectory(siteDir);

    // -------------------------
    // ZIP UPLOAD (SAFE FIX)
    // -------------------------
    var zip = form.Files.FirstOrDefault(f => f.Name == "zip");

    if (zip != null)
    {
        var zipPath = Path.Combine(siteDir, "upload.zip");

        await using (var fs = File.Create(zipPath))
        {
            await zip.CopyToAsync(fs);
        }

        ZipFile.ExtractToDirectory(zipPath, siteDir, overwriteFiles: true);
        File.Delete(zipPath);
    }

    // -------------------------
    // FOLDER UPLOAD
    // -------------------------
    var files = form.Files.Where(f => f.Name == "files");

    foreach (var file in files)
    {
        var target = Path.Combine(siteDir, file.FileName);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        await using var fs = File.Create(target);
        await file.CopyToAsync(fs);
    }

    return Results.Ok(new
    {
        ok = true,
        url = "/" + name
    });
});

// =========================
// API: SET HOMEPAGE
// =========================
app.MapPost("/api/set-home", async (HttpRequest req) =>
{
    var data = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(req.Body);

    var site = Slug(data["site"]);
    var home = data["home"];

    var configPath = Path.Combine(root, site, ".hostify.json");

    await File.WriteAllTextAsync(configPath,
        JsonSerializer.Serialize(new { home })
    );

    return Results.Ok(new { ok = true });
});

// =========================
// SITE ROUTE (MUST BE LAST)
// =========================
app.Map("/{site}/{**path}", async (HttpContext ctx, string site, string? path) =>
{
    // 🔴 BLOCK API LEAK INTO SITE ROUTE
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        ctx.Response.StatusCode = 404;
        return;
    }

    var siteDir = Path.Combine(root, site);

    if (!Directory.Exists(siteDir))
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("Site not found");
        return;
    }

    string home = "index.html";

    var configPath = Path.Combine(siteDir, ".hostify.json");

    if (File.Exists(configPath))
    {
        var json = await File.ReadAllTextAsync(configPath);
        var cfg = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

        if (cfg != null && cfg.ContainsKey("home"))
            home = cfg["home"];
    }

    var file = string.IsNullOrEmpty(path) ? home : path;
    var fullPath = Path.Combine(siteDir, file);

    if (!File.Exists(fullPath))
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("File not found");
        return;
    }

    var ext = Path.GetExtension(fullPath);

    ctx.Response.ContentType = ext switch
    {
        ".html" => "text/html",
        ".css" => "text/css",
        ".js" => "application/javascript",
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        _ => "text/plain"
    };

    await ctx.Response.SendFileAsync(fullPath);
});
app.MapDelete("/api/delete-site/{site}", (string site) =>
{
    var safeSite = Slug(site);
    var dir = Path.Combine(root, safeSite);

    if (!Directory.Exists(dir))
        return Results.NotFound(new { ok = false, error = "Site not found" });

    Directory.Delete(dir, true);

    return Results.Ok(new { ok = true });
});


app.MapDelete("/api/delete-file/{site}/{**file}", (string site, string file) =>
{
    var safeSite = Slug(site);
    var dir = Path.Combine(root, safeSite);

    if (!Directory.Exists(dir))
        return Results.NotFound(new { ok = false, error = "Site not found" });

    var fullPath = Path.Combine(dir, file);

    if (!File.Exists(fullPath))
        return Results.NotFound(new { ok = false, error = "File not found" });

    File.Delete(fullPath);

    return Results.Ok(new { ok = true });
});

app.MapPost("/api/rename-file/{site}", async (HttpRequest req, string site) =>
{
    var safeSite = Slug(site);
    var dir = Path.Combine(root, safeSite);

    if (!Directory.Exists(dir))
        return Results.NotFound(new { ok = false, error = "Site not found" });

    var data = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(req.Body);

    if (data == null || !data.ContainsKey("old") || !data.ContainsKey("new"))
        return Results.BadRequest(new { ok = false, error = "Invalid payload" });

    var oldFile = data["old"];
    var newFile = data["new"];

    var oldPath = Path.Combine(dir, oldFile);
    var newPath = Path.Combine(dir, newFile);

    if (!File.Exists(oldPath))
        return Results.NotFound(new { ok = false, error = "Old file not found" });

    Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);

    File.Move(oldPath, newPath, overwrite: true);

    return Results.Ok(new { ok = true });
});
app.Run();