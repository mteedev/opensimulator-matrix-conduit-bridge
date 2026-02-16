/*
 * Copyright (c) Fiona Sweet <fiona@pobox.holoneon.com>
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Text.Json;

const string HS_TOKEN = "KEY";
const string AS_TOKEN = "KEY";

const string MATRIX_BASE = "http://127.0.0.1:8008"; // Synapse client API
const string HOMESERVER_NAME = "matrix.holoneon.com"; // USE yours
const string OS_BRIDGE_SECRET = "KEY";
const string AV_PIC_PREFIX = "https://holoneon.com/av.php?uuid="; // USE yours
//const string ROBUST_URL = "http://10.99.0.1:8003"; // move from Robust to Region Server
const string REGION_URL = "http://10.99.0.1:9000"; // USE yours

//see listen ip / port below!!!!

string mysqlConn =
    "Server=127.0.0.1;" +
    "Database=dbname;" +
    "User ID=dbuser;" +
    "Password=assword" +
    "SslMode=None;";

bool ValidateToken(HttpRequest req)
{
    if (!req.Headers.TryGetValue("Authorization", out var auth))
        return false;

    var header = auth.ToString();

    if (!header.StartsWith("Bearer "))
        return false;

    var token = header.Substring("Bearer ".Length);

    return CryptographicEquals(token, HS_TOKEN);
}

static bool CryptographicEquals(string? a, string? b)
{
    if (a is null || b is null)
        return false;

    if (a.Length != b.Length)
        return false;

    var result = 0;
    for (int i = 0; i < a.Length; i++)
        result |= a[i] ^ b[i];

    return result == 0;
}

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    // Synapse → Bridge (AppService)
    options.ListenLocalhost(9009);

    // OpenSim → Bridge (ingest)
    options.Listen(System.Net.IPAddress.Parse("10.99.0.5"), 9010);
});

builder.Services.AddSingleton(
    new BridgeService(
        mysqlConn,
        MATRIX_BASE,
        HOMESERVER_NAME,
        AS_TOKEN,
        AV_PIC_PREFIX,
        REGION_URL,
        OS_BRIDGE_SECRET
        ));

var app = builder.Build();

/* DEBUG ALL REQS */

/*
app.Use(async (context, next) =>
{
    Console.WriteLine("---- INCOMING REQUEST ----");
    Console.WriteLine($"{context.Request.Method} {context.Request.Path}");

    foreach (var header in context.Request.Headers)
        Console.WriteLine($"{header.Key}: {header.Value}");

    await next();
});
*/

app.MapPost("/admin/bridge/resync", async (HttpRequest req, BridgeService bridge, EnableBridgeRequest request) =>
{
    if (!req.Headers.TryGetValue("X-Bridge-Secret", out var secretValues) ||
        secretValues.Count != 1 ||
        string.IsNullOrEmpty(secretValues[0]) ||
        !CryptographicEquals(secretValues[0], OS_BRIDGE_SECRET))
    {
        return Results.Unauthorized();
    }

    await bridge.ResyncGroupAsync(request.GroupUuid);

    return Results.Ok(new { status = "resynced" });
});

app.MapPost("/admin/bridge/enable", async (BridgeService bridge, EnableBridgeRequest req) =>
{
    // Later you’ll validate req.RequestingAvatarUuid is group owner.
    var roomId = await bridge.EnableBridgeAsync(req.GroupUuid, req.GroupName, req.FounderAvatarUuid);
    return Results.Ok(new { roomId });
});

// --- AppService: User existence check ---
app.MapGet("/_matrix/app/v1/users/{userId}", (HttpRequest req, string userId) =>
{
    if (!ValidateToken(req)) return Results.Unauthorized();

    Console.WriteLine($"User existence check: {userId}");
    return Results.Ok();
});

// --- AppService: Transaction endpoint ---
app.MapPut("/_matrix/app/v1/transactions/{txnId}", async (HttpRequest req, string txnId, BridgeService bridge) =>
{
    Console.WriteLine($"MAPPUT Received PUT {txnId}");

    if (!ValidateToken(req))
        return Results.Unauthorized();

    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();

    Console.WriteLine($"Transaction {txnId}");
    //Console.WriteLine(body);

    await bridge.HandleMatrixTransactionAsync(body);

    // Always return empty JSON object
    return Results.Json(new { });
});

app.MapPost("/transactions/{txnId}", async (
    string txnId,
    HttpRequest req,
    BridgeService bridge) =>
{
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();

    await bridge.HandleMatrixTransactionAsync(body);

    return Results.Ok();
});

// --- OpenSim ingest endpoint ---
app.MapPost("/os/event", async (HttpRequest req, BridgeService bridge, OsEvent? evt) =>
{

    if (!req.Headers.TryGetValue("X-Bridge-Secret", out var secretValues) ||
        secretValues.Count != 1)
    {
        return Results.Unauthorized();
    }
    
    var providedSecret = secretValues[0];
    
    if (string.IsNullOrEmpty(providedSecret) ||
        !CryptographicEquals(providedSecret, OS_BRIDGE_SECRET))
    {
        return Results.Unauthorized();
    }
    
    if (evt == null)
        return Results.BadRequest("Invalid payload");

    if (evt.type == "group_message")
    {
        await bridge.RelayMessageFromOpenSimAsync(
		evt.group_uuid, 
		evt.from_uuid, 
		evt.from_name, 
		evt.message
	);
        return Results.Ok();
    }

    return Results.BadRequest("Unknown event type");
});

app.Run();

record EnableBridgeRequest(string GroupUuid, string GroupName, string FounderAvatarUuid);
public record OsEvent(string type, string group_uuid, string from_uuid, string from_name, string message);


