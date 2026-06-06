using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using SocketSample.Shared;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
if (String.IsNullOrEmpty(builder.Configuration["urls"]) &&
    String.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://127.0.0.1:0");
}

SampleClientSettings settings = builder.Configuration
    .GetSection("sampleClient")
    .Get<SampleClientSettings>() ?? new SampleClientSettings();

builder.Services.AddSingleton<SampleSocketClientSession>();
builder.Services.AddSingleton(settings);

WebApplication app = builder.Build();

SampleSocketClientSession session = app.Services.GetRequiredService<SampleSocketClientSession>();
session.Configure(app.Services.GetRequiredService<SampleClientSettings>());

app.MapGet("/", () => Results.Content(RenderPage(), "text/html; charset=utf-8"));
app.MapGet("/api/state", (SampleSocketClientSession clientSession) => clientSession.GetState());
app.MapGet("/api/settings", (SampleSocketClientSession clientSession) => clientSession.Settings);
app.MapPost("/api/settings", ([FromBody] SampleClientSettings nextSettings, SampleSocketClientSession clientSession) =>
{
    clientSession.Configure(nextSettings);
    return clientSession.GetState();
});
app.MapPost("/api/connect", async (SampleSocketClientSession clientSession) =>
{
    await clientSession.ConnectAsync();
    return clientSession.GetState();
});
app.MapPost("/api/send", async ([FromBody] SendMessageRequest request, SampleSocketClientSession clientSession) =>
{
    await clientSession.SendMessageAsync(request.TargetClientId, request.Content);
    return clientSession.GetState();
});
app.MapPost("/api/disconnect", (SampleSocketClientSession clientSession) =>
{
    clientSession.Disconnect();
    return clientSession.GetState();
});

app.Run();

static string RenderPage()
{
    StringBuilder html = new();
    html.AppendLine("<!doctype html>");
    html.AppendLine("<html lang=\"ko\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
    html.AppendLine("<title>Socket Sample Client</title>");
    html.AppendLine("<style>");
    html.AppendLine("body{font-family:-apple-system,BlinkMacSystemFont,Segoe UI,sans-serif;margin:0;background:#f6f7f9;color:#1f2937}");
    html.AppendLine("main{max-width:920px;margin:0 auto;padding:24px}h1{font-size:24px;margin:0 0 18px}");
    html.AppendLine(".grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}.panel{background:white;border:1px solid #d7dce2;border-radius:8px;padding:16px;margin-bottom:14px}");
    html.AppendLine("label{display:block;font-size:13px;font-weight:600;margin-bottom:6px}input,textarea{box-sizing:border-box;width:100%;border:1px solid #c9d1db;border-radius:6px;padding:10px;font-size:14px}textarea{min-height:82px}");
    html.AppendLine("button{border:0;border-radius:6px;background:#2563eb;color:white;padding:10px 12px;font-weight:700;cursor:pointer}button.secondary{background:#4b5563}.actions{display:flex;gap:8px;flex-wrap:wrap}.status{white-space:pre-wrap;background:#111827;color:#e5e7eb;border-radius:8px;padding:12px;min-height:92px}");
    html.AppendLine("@media(max-width:720px){.grid{grid-template-columns:1fr}}");
    html.AppendLine("</style></head><body><main>");
    html.AppendLine("<h1>Socket Sample Client</h1>");
    html.AppendLine("<section class=\"panel\"><div class=\"grid\">");
    html.AppendLine("<div><label>Client ID</label><input id=\"clientId\" type=\"number\" min=\"1\"></div>");
    html.AppendLine("<div><label>Client Name</label><input id=\"clientName\"></div>");
    html.AppendLine("<div><label>Server / Control Host</label><input id=\"host\"></div>");
    html.AppendLine("<div><label>Port</label><input id=\"port\" type=\"number\" min=\"0\" max=\"65535\"></div>");
    html.AppendLine("<div><label>Receive Timeout Seconds</label><input id=\"receiveTimeoutSeconds\" type=\"number\" min=\"1\"></div>");
    html.AppendLine("<div><label><input id=\"useControlServer\" type=\"checkbox\" style=\"width:auto\"> Use ControlServer route</label></div>");
    html.AppendLine("</div><div class=\"actions\" style=\"margin-top:14px\"><button onclick=\"saveSettings()\">Save Settings</button><button onclick=\"connect()\">Connect</button><button class=\"secondary\" onclick=\"disconnect()\">Disconnect</button></div></section>");
    html.AppendLine("<section class=\"panel\"><div class=\"grid\"><div><label>Target Client ID</label><input id=\"targetClientId\" type=\"number\" min=\"1\"></div><div><label>Message</label><textarea id=\"content\">hello</textarea></div></div><div class=\"actions\" style=\"margin-top:14px\"><button onclick=\"sendMessage()\">Send</button></div></section>");
    html.AppendLine("<section class=\"panel\"><label>Status</label><div id=\"status\" class=\"status\"></div></section>");
    html.AppendLine("<script>");
    html.AppendLine("async function json(url,opt){const r=await fetch(url,Object.assign({headers:{'Content-Type':'application/json'}},opt||{}));return await r.json();}");
    html.AppendLine("function el(id){return document.getElementById(id)}");
    html.AppendLine("function settings(){return {clientId:+el('clientId').value,clientName:el('clientName').value,host:el('host').value,port:+el('port').value,useControlServer:el('useControlServer').checked,receiveTimeoutSeconds:+el('receiveTimeoutSeconds').value,security:{profile:'EndToEndTls',transportMode:'Tls',tlsProtocol:'Tls13',requireTls13:true,requireClientCertificate:false,certificateDirectory:'',certificatePasswordEnvironmentVariable:'SOCKET_CERTIFICATE_PASSWORD',certificateRenewBeforeDays:30,rootCertificateLifetimeYears:10,moduleCertificateLifetimeYears:2,authenticationTimeoutMilliseconds:30000,messageEncryptionSecretEnvironmentVariable:'SOCKET_MESSAGE_SECRET',trustedNetwork:false},socketOptions:{connectTimeoutSeconds:30,readTimeoutSeconds:30,writeTimeoutSeconds:30}}}");
    html.AppendLine("function show(s){el('status').textContent=JSON.stringify(s,null,2)}");
    html.AppendLine("async function load(){const s=await json('/api/settings');el('clientId').value=s.clientId;el('clientName').value=s.clientName;el('host').value=s.host;el('port').value=s.port;el('useControlServer').checked=s.useControlServer;el('receiveTimeoutSeconds').value=s.receiveTimeoutSeconds;show(await json('/api/state'));}");
    html.AppendLine("async function saveSettings(){show(await json('/api/settings',{method:'POST',body:JSON.stringify(settings())}))}");
    html.AppendLine("async function connect(){show(await json('/api/connect',{method:'POST'}))}");
    html.AppendLine("async function disconnect(){show(await json('/api/disconnect',{method:'POST'}))}");
    html.AppendLine("async function sendMessage(){show(await json('/api/send',{method:'POST',body:JSON.stringify({targetClientId:+targetClientId.value,content:content.value})}))}");
    html.AppendLine("load();");
    html.AppendLine("setInterval(async()=>show(await json('/api/state')),1000);");
    html.AppendLine("</script></main></body></html>");
    return html.ToString();
}

internal sealed class SendMessageRequest
{
    public uint TargetClientId { get; init; }

    public string Content { get; init; } = "";
}
