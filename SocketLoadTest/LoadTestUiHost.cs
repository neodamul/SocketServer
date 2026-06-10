using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SocketClient.Model;
using SocketCommon.Model;

namespace SocketLoadTest;

internal static class LoadTestUiHost
{
    public static async Task RunAsync(LoadTestOptions options)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{options.UiPort}");
        builder.Services.AddSingleton(new LoadTestUiService(options));

        WebApplication app = builder.Build();
        app.MapGet("/", () => Results.Content(RenderPage(options), "text/html; charset=utf-8"));
        app.MapGet("/api/state", (LoadTestUiService service) => service.GetState());
        app.MapPost("/api/start", async (LoadTestUiStartRequest request, LoadTestUiService service) =>
        {
            await service.ApplyAsync(request);
            return service.GetState();
        });
        app.MapPost("/api/stop", async (LoadTestUiService service) =>
        {
            await service.StopAsync();
            return service.GetState();
        });

        await app.StartAsync();
        foreach (string url in app.Urls)
        {
            Console.WriteLine($"SocketLoadTest UI listening on {url}");
        }

        await app.WaitForShutdownAsync();
    }

    private static string RenderPage(LoadTestOptions options)
    {
        StringBuilder html = new();
        html.AppendLine("<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<title>SocketLoadTest</title>");
        html.AppendLine("<style>");
        html.AppendLine(":root{--bg:#0b0f17;--panel:#141b2d;--panel2:#1a2336;--line:#243049;--soft:#1d2740;--text:#e7ecf5;--muted:#9aa7c2;--muted2:#6b7894;--accent:#5b8cff;--accent2:#7b5bff;--ok:#34d399}");
        html.AppendLine("*{box-sizing:border-box}body{margin:0;background:radial-gradient(1200px 600px at 80% -10%,#16203a 0%,var(--bg) 55%);color:var(--text);font-family:-apple-system,BlinkMacSystemFont,Segoe UI,Inter,sans-serif;font-size:14px}");
        html.AppendLine("main{max-width:1180px;margin:0 auto;padding:20px 24px 40px}");
        html.AppendLine(".topbar{display:flex;align-items:center;gap:12px;padding:6px 0 16px;border-bottom:1px solid var(--soft);margin-bottom:18px}.brand-logo{width:34px;height:34px;border-radius:9px;background:linear-gradient(135deg,var(--accent),var(--accent2));display:grid;place-items:center;box-shadow:0 6px 18px rgba(91,140,255,.4);flex:none}.brand-logo svg{width:18px;height:18px}h1{font-size:17px;margin:0;letter-spacing:.2px}.brand .sub{font-size:12px;color:var(--muted2);margin-top:2px}");
        html.AppendLine(".band{background:linear-gradient(180deg,var(--panel),#111726);border:1px solid var(--line);border-radius:12px;padding:16px;margin-bottom:14px;box-shadow:0 8px 24px rgba(0,0,0,.35)}h2{font-size:13.5px;letter-spacing:.3px}");
        html.AppendLine(".grid{display:grid;grid-template-columns:repeat(6,minmax(0,1fr));gap:12px}.metrics{display:grid;grid-template-columns:repeat(6,minmax(0,1fr));gap:12px;margin-bottom:14px}");
        html.AppendLine(".metric{border:1px solid var(--line);border-radius:12px;padding:14px;background:linear-gradient(180deg,var(--panel),#111726);font-size:11px;text-transform:uppercase;letter-spacing:.6px;color:var(--muted2);font-weight:600;box-shadow:0 8px 24px rgba(0,0,0,.35)}.metric b{display:block;font-size:24px;margin-top:6px;color:var(--text);font-weight:700;letter-spacing:-.4px;font-variant-numeric:tabular-nums;text-transform:none}");
        html.AppendLine("label{display:block;font-size:11px;font-weight:600;text-transform:uppercase;letter-spacing:.5px;color:var(--muted2);margin-bottom:6px}");
        html.AppendLine("input{box-sizing:border-box;width:100%;border:1px solid var(--line);border-radius:8px;padding:9px;font-size:14px;background:var(--panel2);color:var(--text)}input[type=checkbox]{width:auto;height:18px}");
        html.AppendLine("button{border:0;border-radius:8px;background:var(--accent);color:#fff;padding:9px 14px;font-weight:700;cursor:pointer;font-size:13px}button.secondary{background:var(--panel2);color:var(--muted);border:1px solid var(--line)}");
        html.AppendLine("table{width:100%;border-collapse:collapse;font-size:13px}th{font-size:11px;text-transform:uppercase;letter-spacing:.6px;color:var(--muted2);text-align:left;padding:10px;border-bottom:1px solid var(--soft)}td{text-align:left;border-top:1px solid var(--soft);padding:10px;font-variant-numeric:tabular-nums}");
        html.AppendLine("pre{white-space:pre-wrap;background:#0c111c;color:var(--muted);border:1px solid var(--soft);border-radius:8px;padding:12px;min-height:120px;font-size:12px}");
        html.AppendLine(".cl-details{margin-top:8px}.cl-details summary{cursor:pointer;color:var(--muted2);font-size:12px;font-weight:600;padding:8px 0;user-select:none}.cl-details summary:hover{color:var(--text)}#clientsJson{margin-top:8px}");
        html.AppendLine(".mini{padding:5px 11px;font-size:12px;border-radius:7px}.modal-overlay{position:fixed;inset:0;background:rgba(4,8,16,.62);display:grid;place-items:center;z-index:50;padding:20px}.modal{width:min(560px,94vw);max-height:80vh;overflow:auto;background:linear-gradient(180deg,var(--panel),#111726);border:1px solid var(--line);border-radius:14px;box-shadow:0 24px 60px rgba(0,0,0,.55);padding:16px}.modal-head{display:flex;align-items:center;justify-content:space-between;gap:12px;margin-bottom:12px}.modal-head h2{margin:0;font-size:15px}");
        html.AppendLine("@media(max-width:920px){.grid,.metrics{grid-template-columns:repeat(2,minmax(0,1fr))}}");
        html.AppendLine("</style></head><body><main>");
        html.AppendLine("<div class=\"topbar\"><div class=\"brand-logo\"><svg viewBox=\"0 0 24 24\" fill=\"none\"><path d=\"M4 7l8-4 8 4-8 4-8-4z\" fill=\"#fff\" opacity=\".95\"/><path d=\"M4 12l8 4 8-4M4 17l8 4 8-4\" stroke=\"#fff\" stroke-width=\"1.6\" stroke-linecap=\"round\" stroke-linejoin=\"round\" opacity=\".8\"/></svg></div><div class=\"brand\"><h1>SocketLoadTest</h1><div class=\"sub\">Web UI · load generator</div></div></div>");
        html.AppendLine("<section class=\"band\"><div class=\"grid\">");
        html.AppendLine($"<div><label>Clients</label><input id=\"clients\" type=\"number\" min=\"1\" value=\"{options.Clients}\"></div>");
        html.AppendLine($"<div><label>Start Client ID</label><input id=\"startClientId\" type=\"number\" min=\"1\" value=\"{options.StartClientId}\"></div>");
        html.AppendLine($"<div><label>Batch Size</label><input id=\"batchSize\" type=\"number\" min=\"1\" value=\"{options.BatchSize}\"></div>");
        html.AppendLine($"<div><label>Host</label><input id=\"host\" value=\"{WebUtility.HtmlEncode(options.Host)}\"></div>");
        html.AppendLine($"<div><label>Port</label><input id=\"port\" type=\"number\" min=\"0\" max=\"65535\" value=\"{options.Port}\"></div>");
        html.AppendLine($"<div><label>Ramp Delay ms</label><input id=\"rampDelayMilliseconds\" type=\"number\" min=\"0\" value=\"{options.RampDelayMilliseconds}\"></div>");
        html.AppendLine($"<div><label>Use ControlServer</label><input id=\"useControlServer\" type=\"checkbox\"{(options.UseControlServer ? " checked" : string.Empty)}></div>");
        html.AppendLine("</div><div style=\"display:flex;gap:8px;margin-top:12px\"><button onclick=\"start()\">Start</button><button class=\"secondary\" onclick=\"stop()\">Stop</button><button class=\"secondary\" onclick=\"refresh()\">Refresh</button></div></section>");
        html.AppendLine("<section class=\"metrics\" id=\"metrics\"></section>");
        html.AppendLine("<section class=\"band\"><h2 style=\"font-size:16px;margin:0 0 10px\">Target Servers</h2><table><thead><tr><th>Target Server</th><th>Connected Clients</th><th></th></tr></thead><tbody id=\"targets\"></tbody></table></section>");
        html.AppendLine("<section class=\"band\"><h2 style=\"font-size:16px;margin:0 0 10px\">State</h2><pre id=\"state\"></pre><details id=\"clientsDetails\" class=\"cl-details\"><summary id=\"clientsSummary\">clients</summary><pre id=\"clientsJson\"></pre></details></section>");
        html.AppendLine("<div id=\"clientModal\" class=\"modal-overlay\" style=\"display:none\"><div class=\"modal\"><div class=\"modal-head\"><h2 id=\"modalTitle\">Clients</h2><button class=\"secondary mini\" onclick=\"closeClients()\">Close</button></div><table><thead><tr><th>Client ID</th><th>Connected</th></tr></thead><tbody id=\"modalClients\"></tbody></table></div></div>");
        html.AppendLine("<script>");
        html.AppendLine("async function api(url,opt){const r=await fetch(url,Object.assign({headers:{'Content-Type':'application/json'}},opt||{}));return await r.json();}");
        html.AppendLine("function val(id){return document.getElementById(id).value}function checked(id){return document.getElementById(id).checked}");
        html.AppendLine("function metric(label,value){return `<div class='metric'>${label}<b>${value}</b></div>`}");
        html.AppendLine("let lastState=null;");
        html.AppendLine("function renderTargets(){const ts=lastState?lastState.targetServers:[];document.getElementById('targets').innerHTML=ts.map(t=>`<tr><td>${t.targetServer}</td><td>${t.connectedClients}</td><td style=\"text-align:right\"><button class=\"mini\" data-target=\"${t.targetServer}\">View Clients</button></td></tr>`).join('');}");
        html.AppendLine("function openClients(server){const cs=lastState?lastState.clients:[];const rows=cs.filter(c=>String(c.targetServer)===String(server)).sort((a,b)=>Number(a.clientId)-Number(b.clientId)||String(a.clientId).localeCompare(String(b.clientId),undefined,{numeric:true}));document.getElementById('modalTitle').textContent=`Clients · ${server} (${rows.length})`;document.getElementById('modalClients').innerHTML=rows.length?rows.map(c=>`<tr><td>${c.clientId}</td><td>${c.isConnected}</td></tr>`).join(''):'<tr><td colspan=\"2\">No clients</td></tr>';document.getElementById('clientModal').style.display='grid';}");
        html.AppendLine("function closeClients(){document.getElementById('clientModal').style.display='none';}");
        html.AppendLine("function render(s){lastState=s;const cl=Array.isArray(s.clients)?s.clients:[];document.getElementById('state').textContent=JSON.stringify(Object.assign({},s,{clients:cl.length}),null,2);document.getElementById('clientsSummary').textContent=`clients (${cl.length})`;document.getElementById('clientsJson').textContent=JSON.stringify(cl,null,2);const c=s.counters;document.getElementById('metrics').innerHTML=metric('Status',s.status)+metric('Connected Now',s.connectedNow)+metric('Attempted',c.attempted)+metric('Connected Total',c.connected)+metric('Healthcheck OK',c.healthCheckSuccess)+metric('Failures',c.connectFail+c.healthCheckFail+c.registerFail+c.messageFail);renderTargets();}");
        html.AppendLine("document.getElementById('targets').addEventListener('click',function(e){const b=e.target.closest('button[data-target]');if(!b)return;openClients(b.getAttribute('data-target'));});");
        html.AppendLine("document.getElementById('clientModal').addEventListener('click',function(e){if(e.target.id==='clientModal')closeClients();});");
        html.AppendLine("async function refresh(){render(await api('/api/state'))}");
        html.AppendLine("async function start(){render(await api('/api/start',{method:'POST',body:JSON.stringify({clients:+val('clients'),startClientId:+val('startClientId'),batchSize:+val('batchSize'),host:val('host'),port:+val('port'),useControlServer:checked('useControlServer'),rampDelayMilliseconds:+val('rampDelayMilliseconds')} )}))}");
        html.AppendLine("async function stop(){render(await api('/api/stop',{method:'POST'}))}");
        html.AppendLine("refresh();setInterval(refresh,5000);");
        html.AppendLine("</script></main></body></html>");
        return html.ToString();
    }
}

internal sealed class LoadTestUiService
{
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(1);

    private readonly object syncRoot = new();
    private readonly LoadTestOptions defaultOptions;
    private readonly List<LoadTestUiConnectedClient> connectedClients = new();
    private LoadTestCounters counters = new();
    private CancellationTokenSource? cancellation;
    private Task? workerTask;
    private DateTimeOffset? startedAt;
    private DateTimeOffset? stoppedAt;
    private string status = "Idle";
    private string errorMessage = "";
    private int runGeneration;
    private LoadTestOptions? runOptions;
    private HashSet<int>? desiredIds;
    private readonly Dictionary<int, int> pendingClientGenerations = new();
    private readonly HashSet<int> pendingClientReschedules = new();
    private readonly SemaphoreSlim applyGate = new(1, 1);

    public LoadTestUiService(LoadTestOptions defaultOptions)
    {
        this.defaultOptions = defaultOptions;
    }

    public Task StartAsync(LoadTestUiStartRequest request)
    {
        lock (this.syncRoot)
        {
            if (this.workerTask != null && !this.workerTask.IsCompleted)
            {
                return Task.CompletedTask;
            }

            this.DisposeClients();
            this.startedAt = DateTimeOffset.UtcNow;
            this.stoppedAt = null;
            this.status = "Starting";
            this.errorMessage = "";
            this.cancellation?.Dispose();
            this.cancellation = new CancellationTokenSource();
            LoadTestOptions options = this.CreateOptions(request);
            this.runOptions = options;
            this.desiredIds = BuildClientIdSet(options);
            this.pendingClientGenerations.Clear();
            this.pendingClientReschedules.Clear();
            int generation = ++this.runGeneration;
            LoadTestCounters runCounters = new();
            this.counters = runCounters;
            this.workerTask = Task.Run(() => this.RunAsync(options, runCounters, this.cancellation.Token, generation));
            return Task.CompletedTask;
        }
    }

    public async Task ApplyAsync(LoadTestUiStartRequest request)
    {
        await this.applyGate.WaitAsync();
        try
        {
            LoadTestOptions options = this.CreateOptions(request);

            bool running;
            LoadTestOptions? current;
            lock (this.syncRoot)
            {
                running = this.workerTask != null && !this.workerTask.IsCompleted;
                current = this.runOptions;
            }

            // Idle (or no active run) -> normal initial start.
            if (!running || current == null)
            {
                await this.StartAsync(request);
                return;
            }

            // Transport/identity change cannot be applied to live connections -> full restart.
            if (!string.Equals(current.Host, options.Host, StringComparison.Ordinal)
                || current.Port != options.Port
                || current.UseControlServer != options.UseControlServer)
            {
                await this.StopAsync();
                await this.StartAsync(request);
                return;
            }

            // Incremental: same transport, only count/batch/ramp differ.
            HashSet<int> targetIds = BuildClientIdSet(options);

            bool startFresh = false;
            int[] currentIds = Array.Empty<int>();
            HashSet<int> previousDesiredIds = new();
            int generation = 0;
            LoadTestCounters runCounters = this.counters;
            CancellationToken cancellationToken = CancellationToken.None;
            lock (this.syncRoot)
            {
                if (this.workerTask == null || this.workerTask.IsCompleted)
                {
                    startFresh = true;
                }
                else
                {
                    currentIds = this.connectedClients.Select(client => client.ClientId).ToArray();
                    previousDesiredIds = this.desiredIds != null
                        ? new HashSet<int>(this.desiredIds)
                        : new HashSet<int>(currentIds);
                    HashSet<int> removeSet = new(currentIds.Where(id => !targetIds.Contains(id)));
                    for (int index = this.connectedClients.Count - 1; index >= 0; index--)
                    {
                        if (removeSet.Contains(this.connectedClients[index].ClientId))
                        {
                            this.connectedClients[index].Session.Dispose();
                            this.connectedClients.RemoveAt(index);
                        }
                    }

                    generation = this.runGeneration;
                    runCounters = this.counters;
                    cancellationToken = this.cancellation?.Token ?? CancellationToken.None;
                    this.runOptions = options;
                    this.desiredIds = targetIds;
                    this.status = "Holding";
                }
            }

            if (startFresh)
            {
                await this.StartAsync(request);
                return;
            }

            HashSet<int> currentSet = previousDesiredIds.Count > 0
                ? previousDesiredIds
                : new HashSet<int>(currentIds);
            int[] toAdd = targetIds.Where(id => !currentSet.Contains(id)).OrderBy(id => id).ToArray();
            if (toAdd.Length > 0)
            {
                _ = Task.Run(() => this.ConnectAndRegisterClientsAsync(
                    options,
                    runCounters,
                    toAdd,
                    generation,
                    cancellationToken));
            }
        }
        finally
        {
            this.applyGate.Release();
        }
    }

    public async Task StopAsync()
    {
        Task? task;
        lock (this.syncRoot)
        {
            this.status = "Stopping";
            this.runGeneration++;
            this.cancellation?.Cancel();
            task = this.workerTask;
        }

        if (task != null)
        {
            await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        }

        lock (this.syncRoot)
        {
            if (this.workerTask != task)
            {
                return;
            }

            this.DisposeClients();
            this.workerTask = null;
            this.runOptions = null;
            this.desiredIds = null;
            this.pendingClientGenerations.Clear();
            this.pendingClientReschedules.Clear();
            this.status = "Stopped";
            this.stoppedAt = DateTimeOffset.UtcNow;
        }
    }

    public LoadTestUiState GetState()
    {
        lock (this.syncRoot)
        {
            LoadTestUiClientState[] clients = this.connectedClients
                .Select(client => new LoadTestUiClientState(
                    client.ClientId,
                    client.Session.IsConnected && client.Session.IsRegistered,
                    client.Session.ConnectedEndpoint))
                .ToArray();
            LoadTestUiTargetState[] targets = clients
                .Where(client => client.IsConnected)
                .GroupBy(client => client.TargetServer, StringComparer.Ordinal)
                .Select(group => new LoadTestUiTargetState(group.Key, group.Count()))
                .OrderBy(target => target.TargetServer, StringComparer.Ordinal)
                .ToArray();
            bool isRunning = this.workerTask != null && !this.workerTask.IsCompleted;
            return new LoadTestUiState(
                isRunning,
                this.status,
                this.errorMessage,
                this.startedAt,
                this.stoppedAt,
                clients.Count(client => client.IsConnected),
                SnapshotCounters(this.counters),
                targets,
                clients);
        }
    }

    private async Task RunAsync(
        LoadTestOptions options,
        LoadTestCounters runCounters,
        CancellationToken cancellationToken,
        int generation)
    {
        try
        {
            int lastClientId = options.StartClientId + options.Clients - 1;
            for (int firstClientId = options.StartClientId; firstClientId <= lastClientId && !cancellationToken.IsCancellationRequested; firstClientId += options.BatchSize)
            {
                int batchCount = Math.Min(options.BatchSize, lastClientId - firstClientId + 1);
                LoadTestUiClientAttemptResult[] results = await this.ConnectBatchAsync(
                    options,
                    runCounters,
                    firstClientId,
                    batchCount,
                    generation,
                    cancellationToken);
                lock (this.syncRoot)
                {
                    if (!this.IsActiveRun(generation) || cancellationToken.IsCancellationRequested)
                    {
                        DisposeAttemptResults(results);
                        return;
                    }

                    this.AddConnectedResultsLocked(results);
                    this.status = "Holding";
                }

                if (options.RampDelayMilliseconds > 0)
                {
                    await Task.Delay(options.RampDelayMilliseconds, cancellationToken);
                }
            }

            lock (this.syncRoot)
            {
                if (!this.IsActiveRun(generation))
                {
                    return;
                }
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (TaskCanceledException)
        {
        }
        catch (Exception exception)
        {
            lock (this.syncRoot)
            {
                if (!this.IsActiveRun(generation))
                {
                    return;
                }

                this.status = "Failed";
                this.errorMessage = exception.Message;
            }
        }
    }

    private async Task<LoadTestUiClientAttemptResult[]> ConnectBatchAsync(
        LoadTestOptions options,
        LoadTestCounters runCounters,
        int firstClientId,
        int batchCount,
        int generation,
        CancellationToken cancellationToken)
    {
        Task<LoadTestUiClientAttemptResult>[] tasks = new Task<LoadTestUiClientAttemptResult>[batchCount];
        for (int index = 0; index < batchCount; index++)
        {
            int clientId = firstClientId + index;
            tasks[index] = this.TryMarkPendingClient(clientId, generation, requestRescheduleIfPending: false)
                ? Task.Run(() => this.ConnectClientAsync(
                    options,
                    runCounters,
                    clientId,
                    generation,
                    cancellationToken))
                : Task.FromResult(LoadTestUiClientAttemptResult.Failed);
        }

        return await Task.WhenAll(tasks);
    }

    private void AddConnectedResultsLocked(LoadTestUiClientAttemptResult[] results)
    {
        foreach (LoadTestUiClientAttemptResult result in results)
        {
            if (result.Session == null)
            {
                continue;
            }

            // Drop sessions whose clientId is no longer desired (e.g. a concurrent decrement
            // removed it while the initial ramp was still connecting it) or already present.
            if ((this.desiredIds != null && !this.desiredIds.Contains(result.ClientId))
                || this.connectedClients.Any(client => client.ClientId == result.ClientId))
            {
                result.Session.Dispose();
            }
            else
            {
                this.connectedClients.Add(new LoadTestUiConnectedClient(result.ClientId, result.Session));
            }
        }
    }

    private static HashSet<int> BuildClientIdSet(LoadTestOptions options)
    {
        HashSet<int> ids = new();
        int lastClientId = options.StartClientId + options.Clients - 1;
        for (int clientId = options.StartClientId; clientId <= lastClientId; clientId++)
        {
            ids.Add(clientId);
        }

        return ids;
    }

    private async Task ConnectAndRegisterClientsAsync(
        LoadTestOptions options,
        LoadTestCounters runCounters,
        int[] clientIds,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            for (int offset = 0; offset < clientIds.Length && !cancellationToken.IsCancellationRequested; offset += options.BatchSize)
            {
                int batchCount = Math.Min(options.BatchSize, clientIds.Length - offset);
                Task<LoadTestUiClientAttemptResult>[] tasks = new Task<LoadTestUiClientAttemptResult>[batchCount];
                for (int index = 0; index < batchCount; index++)
                {
                    int clientId = clientIds[offset + index];
                    tasks[index] = this.TryMarkPendingClient(clientId, generation, requestRescheduleIfPending: true)
                        ? Task.Run(() => this.ConnectClientAsync(options, runCounters, clientId, generation, cancellationToken))
                        : Task.FromResult(LoadTestUiClientAttemptResult.Failed);
                }

                LoadTestUiClientAttemptResult[] results = await Task.WhenAll(tasks);
                lock (this.syncRoot)
                {
                    if (!this.IsActiveRun(generation) || cancellationToken.IsCancellationRequested)
                    {
                        DisposeAttemptResults(results);
                        return;
                    }

                    this.AddConnectedResultsLocked(results);
                    this.status = "Holding";
                }

                if (options.RampDelayMilliseconds > 0)
                {
                    await Task.Delay(options.RampDelayMilliseconds, cancellationToken);
                }
            }
        }
        catch (TaskCanceledException)
        {
        }
    }

    private async Task<LoadTestUiClientAttemptResult> ConnectClientAsync(
        LoadTestOptions options,
        LoadTestCounters runCounters,
        int clientId,
        int generation,
        CancellationToken cancellationToken)
    {
        bool returnedConnectedSession = false;
        bool terminalRegisterFailure = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                this.IsActiveRun(generation) &&
                this.IsDesiredClient(clientId))
            {
                Interlocked.Increment(ref runCounters.Attempted);
                SocketClientSession session = new();
                session.HealthCheckReceived += _ => Interlocked.Increment(ref runCounters.HealthCheckSuccess);
                try
                {
                    bool connected = options.UseControlServer
                        ? await session.ConnectAndRegisterAsync(
                            clientId,
                            $"ui-client-{clientId}",
                            options.Host,
                            options.Port,
                            true,
                            HealthCheckProtocol.KeepAliveIntervalSeconds,
                            30,
                            90)
                        : await session.ConnectAndRegisterAsync(
                            clientId,
                            $"ui-client-{clientId}",
                            options.Host,
                            options.Port,
                            false,
                            HealthCheckProtocol.KeepAliveIntervalSeconds,
                            30,
                            90);
                    if (connected)
                    {
                        if (!this.IsDesiredClient(clientId))
                        {
                            session.Dispose();
                            return new LoadTestUiClientAttemptResult(clientId, null);
                        }

                        Interlocked.Increment(ref runCounters.Connected);
                        if (cancellationToken.IsCancellationRequested || !this.IsActiveRun(generation))
                        {
                            session.Dispose();
                            return new LoadTestUiClientAttemptResult(clientId, null);
                        }

                        returnedConnectedSession = true;
                        return new LoadTestUiClientAttemptResult(clientId, session);
                    }

                    if (session.LastFailure == SocketClientSessionFailure.Register)
                    {
                        Interlocked.Increment(ref runCounters.RegisterFail);
                        terminalRegisterFailure = true;
                        session.Dispose();
                        return new LoadTestUiClientAttemptResult(clientId, null);
                    }

                    session.Dispose();
                }
                catch (Exception exception) when (exception is TimeoutException or InvalidOperationException or IOException)
                {
                    session.Dispose();
                }

                try
                {
                    await Task.Delay(ConnectRetryDelay, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }

            return LoadTestUiClientAttemptResult.Failed;
        }
        finally
        {
            bool canReschedule = !returnedConnectedSession &&
                !terminalRegisterFailure &&
                !cancellationToken.IsCancellationRequested &&
                this.IsActiveRun(generation);
            if (this.ClearPendingClient(clientId, generation, canReschedule) &&
                this.TryMarkPendingClient(clientId, generation, requestRescheduleIfPending: false))
            {
                _ = Task.Run(() => this.ConnectAndStoreClientAsync(
                    options,
                    runCounters,
                    clientId,
                    generation,
                    cancellationToken));
            }
        }
    }

    private async Task ConnectAndStoreClientAsync(
        LoadTestOptions options,
        LoadTestCounters runCounters,
        int clientId,
        int generation,
        CancellationToken cancellationToken)
    {
        LoadTestUiClientAttemptResult result;
        try
        {
            result = await this.ConnectClientAsync(options, runCounters, clientId, generation, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            lock (this.syncRoot)
            {
                if (!this.IsActiveRun(generation))
                {
                    return;
                }

                this.status = "Failed";
                this.errorMessage = exception.Message;
            }

            return;
        }

        lock (this.syncRoot)
        {
            if (!this.IsActiveRun(generation) || cancellationToken.IsCancellationRequested)
            {
                result.Session?.Dispose();
                return;
            }

            this.AddConnectedResultsLocked(new[] { result });
            this.status = "Holding";
        }
    }

    private bool IsDesiredClient(int clientId)
    {
        lock (this.syncRoot)
        {
            return this.desiredIds == null || this.desiredIds.Contains(clientId);
        }
    }

    private bool TryMarkPendingClient(int clientId, int generation, bool requestRescheduleIfPending)
    {
        lock (this.syncRoot)
        {
            if (this.pendingClientGenerations.TryGetValue(clientId, out int pendingGeneration) &&
                pendingGeneration == generation)
            {
                if (requestRescheduleIfPending)
                {
                    this.pendingClientReschedules.Add(clientId);
                }

                return false;
            }

            this.pendingClientGenerations[clientId] = generation;
            this.pendingClientReschedules.Remove(clientId);
            return true;
        }
    }

    private bool ClearPendingClient(int clientId, int generation, bool canReschedule)
    {
        lock (this.syncRoot)
        {
            if (this.pendingClientGenerations.TryGetValue(clientId, out int pendingGeneration) &&
                pendingGeneration == generation)
            {
                this.pendingClientGenerations.Remove(clientId);
                bool shouldReschedule = canReschedule &&
                    this.runGeneration == generation &&
                    this.desiredIds != null &&
                    this.desiredIds.Contains(clientId) &&
                    this.pendingClientReschedules.Remove(clientId);
                if (!shouldReschedule)
                {
                    this.pendingClientReschedules.Remove(clientId);
                }

                return shouldReschedule;
            }

            return false;
        }
    }

    private LoadTestOptions CreateOptions(LoadTestUiStartRequest request)
    {
        int clients = Math.Max(1, request.Clients ?? this.defaultOptions.Clients);
        return this.defaultOptions with
        {
            Clients = clients,
            StartClientId = Math.Max(1, request.StartClientId ?? this.defaultOptions.StartClientId),
            BatchSize = Math.Max(1, request.BatchSize ?? Math.Min(clients, this.defaultOptions.BatchSize)),
            Host = string.IsNullOrWhiteSpace(request.Host) ? this.defaultOptions.Host : request.Host,
            Port = request.Port ?? this.defaultOptions.Port,
            UseControlServer = request.UseControlServer ?? this.defaultOptions.UseControlServer,
            ExternalServer = request.UseControlServer ?? this.defaultOptions.UseControlServer,
            RampDelayMilliseconds = Math.Max(0, request.RampDelayMilliseconds ?? this.defaultOptions.RampDelayMilliseconds),
            HoldSeconds = int.MaxValue,
            ExpectedConnected = clients,
            MessageTest = false
        };
    }

    private void DisposeClients()
    {
        foreach (LoadTestUiConnectedClient client in this.connectedClients)
        {
            client.Session.Dispose();
        }

        this.connectedClients.Clear();
    }

    private bool IsActiveRun(int generation)
    {
        return this.runGeneration == generation;
    }

    private static void DisposeAttemptResults(IEnumerable<LoadTestUiClientAttemptResult> results)
    {
        foreach (LoadTestUiClientAttemptResult result in results)
        {
            result.Session?.Dispose();
        }
    }

    private static LoadTestUiCounters SnapshotCounters(LoadTestCounters counters)
    {
        return new LoadTestUiCounters(
            Volatile.Read(ref counters.Attempted),
            Volatile.Read(ref counters.Connected),
            Volatile.Read(ref counters.ConnectFail),
            Volatile.Read(ref counters.HealthCheckSuccess),
            Volatile.Read(ref counters.HealthCheckFail),
            Volatile.Read(ref counters.RegisterFail),
            Volatile.Read(ref counters.MessageAttempted),
            Volatile.Read(ref counters.MessageSuccess),
            Volatile.Read(ref counters.MessageFail));
    }
}

internal sealed record LoadTestUiClientAttemptResult(int ClientId, SocketClientSession? Session)
{
    public static LoadTestUiClientAttemptResult Failed { get; } = new(0, null);
}

internal sealed record LoadTestUiConnectedClient(int ClientId, SocketClientSession Session);

internal sealed class LoadTestUiStartRequest
{
    public int? Clients { get; init; }

    public int? StartClientId { get; init; }

    public int? BatchSize { get; init; }

    public string? Host { get; init; }

    public int? Port { get; init; }

    public bool? UseControlServer { get; init; }

    public int? RampDelayMilliseconds { get; init; }
}

internal sealed record LoadTestUiState(
    bool IsRunning,
    string Status,
    string ErrorMessage,
    DateTimeOffset? StartedAt,
    DateTimeOffset? StoppedAt,
    int ConnectedNow,
    LoadTestUiCounters Counters,
    IReadOnlyList<LoadTestUiTargetState> TargetServers,
    IReadOnlyList<LoadTestUiClientState> Clients);

internal sealed record LoadTestUiCounters(
    int Attempted,
    int Connected,
    int ConnectFail,
    int HealthCheckSuccess,
    int HealthCheckFail,
    int RegisterFail,
    int MessageAttempted,
    int MessageSuccess,
    int MessageFail);

internal sealed record LoadTestUiTargetState(string TargetServer, int ConnectedClients);

internal sealed record LoadTestUiClientState(int ClientId, bool IsConnected, string TargetServer);
