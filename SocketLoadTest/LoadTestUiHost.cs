using System.Diagnostics;
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
        app.MapGet("/", () => Results.Content(RenderPage(), "text/html; charset=utf-8"));
        app.MapGet("/api/state", (LoadTestUiService service) => service.GetState());
        app.MapPost("/api/start", async (LoadTestUiStartRequest request, LoadTestUiService service) =>
        {
            await service.StartAsync(request);
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

    private static string RenderPage()
    {
        StringBuilder html = new();
        html.AppendLine("<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<title>SocketLoadTest</title>");
        html.AppendLine("<style>");
        html.AppendLine("body{margin:0;background:#f5f6f8;color:#1f2937;font-family:-apple-system,BlinkMacSystemFont,Segoe UI,sans-serif}");
        html.AppendLine("main{max-width:1180px;margin:0 auto;padding:20px}h1{font-size:22px;margin:0 0 14px}.band{background:white;border:1px solid #d7dce2;border-radius:8px;padding:14px;margin-bottom:12px}");
        html.AppendLine(".grid{display:grid;grid-template-columns:repeat(6,minmax(0,1fr));gap:10px}.metrics{display:grid;grid-template-columns:repeat(6,minmax(0,1fr));gap:10px}.metric{border:1px solid #d7dce2;border-radius:8px;padding:10px;background:#fbfcfd}.metric b{display:block;font-size:22px;margin-top:4px}");
        html.AppendLine("label{display:block;font-size:12px;font-weight:700;margin-bottom:5px}input{box-sizing:border-box;width:100%;border:1px solid #c9d1db;border-radius:6px;padding:9px;font-size:14px}button{border:0;border-radius:6px;background:#2563eb;color:white;padding:10px 12px;font-weight:700;cursor:pointer}button.secondary{background:#4b5563}");
        html.AppendLine("table{width:100%;border-collapse:collapse;font-size:13px}th,td{text-align:left;border-bottom:1px solid #e5e7eb;padding:8px}th{font-size:12px;color:#4b5563}pre{white-space:pre-wrap;background:#111827;color:#e5e7eb;border-radius:8px;padding:10px;min-height:120px}");
        html.AppendLine("@media(max-width:920px){.grid,.metrics{grid-template-columns:repeat(2,minmax(0,1fr))}}");
        html.AppendLine("</style></head><body><main>");
        html.AppendLine("<h1>SocketLoadTest</h1>");
        html.AppendLine("<section class=\"band\"><div class=\"grid\">");
        html.AppendLine("<div><label>Clients</label><input id=\"clients\" type=\"number\" min=\"1\" value=\"4\"></div>");
        html.AppendLine("<div><label>Batch Size</label><input id=\"batchSize\" type=\"number\" min=\"1\" value=\"4\"></div>");
        html.AppendLine("<div><label>Host</label><input id=\"host\" value=\"127.0.0.1\"></div>");
        html.AppendLine("<div><label>Port</label><input id=\"port\" type=\"number\" min=\"0\" max=\"65535\" value=\"56200\"></div>");
        html.AppendLine("<div><label>Ramp Delay ms</label><input id=\"rampDelayMilliseconds\" type=\"number\" min=\"0\" value=\"0\"></div>");
        html.AppendLine("<div><label>Use ControlServer</label><input id=\"useControlServer\" type=\"checkbox\" checked></div>");
        html.AppendLine("</div><div style=\"display:flex;gap:8px;margin-top:12px\"><button onclick=\"start()\">Start</button><button class=\"secondary\" onclick=\"stop()\">Stop</button><button class=\"secondary\" onclick=\"refresh()\">Refresh</button></div></section>");
        html.AppendLine("<section class=\"metrics\" id=\"metrics\"></section>");
        html.AppendLine("<section class=\"band\"><h2 style=\"font-size:16px;margin:0 0 10px\">Target Servers</h2><table><thead><tr><th>Target Server</th><th>Connected Clients</th></tr></thead><tbody id=\"targets\"></tbody></table></section>");
        html.AppendLine("<section class=\"band\"><h2 style=\"font-size:16px;margin:0 0 10px\">Clients</h2><table><thead><tr><th>Client ID</th><th>Connected</th><th>Target Server</th></tr></thead><tbody id=\"clientsTable\"></tbody></table></section>");
        html.AppendLine("<section class=\"band\"><h2 style=\"font-size:16px;margin:0 0 10px\">State</h2><pre id=\"state\"></pre></section>");
        html.AppendLine("<script>");
        html.AppendLine("async function api(url,opt){const r=await fetch(url,Object.assign({headers:{'Content-Type':'application/json'}},opt||{}));return await r.json();}");
        html.AppendLine("function val(id){return document.getElementById(id).value}function checked(id){return document.getElementById(id).checked}");
        html.AppendLine("function metric(label,value){return `<div class='metric'>${label}<b>${value}</b></div>`}");
        html.AppendLine("function render(s){document.getElementById('state').textContent=JSON.stringify(s,null,2);const c=s.counters;document.getElementById('metrics').innerHTML=metric('Status',s.status)+metric('Connected Now',s.connectedNow)+metric('Attempted',c.attempted)+metric('Connected Total',c.connected)+metric('Healthcheck OK',c.healthCheckSuccess)+metric('Failures',c.connectFail+c.healthCheckFail+c.registerFail+c.messageFail);document.getElementById('targets').innerHTML=s.targetServers.map(t=>`<tr><td>${t.targetServer}</td><td>${t.connectedClients}</td></tr>`).join('');document.getElementById('clientsTable').innerHTML=s.clients.map(c=>`<tr><td>${c.clientId}</td><td>${c.isConnected}</td><td>${c.targetServer}</td></tr>`).join('')}");
        html.AppendLine("async function refresh(){render(await api('/api/state'))}");
        html.AppendLine("async function start(){render(await api('/api/start',{method:'POST',body:JSON.stringify({clients:+val('clients'),batchSize:+val('batchSize'),host:val('host'),port:+val('port'),useControlServer:checked('useControlServer'),rampDelayMilliseconds:+val('rampDelayMilliseconds')} )}))}");
        html.AppendLine("async function stop(){render(await api('/api/stop',{method:'POST'}))}");
        html.AppendLine("refresh();setInterval(refresh,5000);");
        html.AppendLine("</script></main></body></html>");
        return html.ToString();
    }
}

internal sealed class LoadTestUiService
{
    private readonly object syncRoot = new();
    private readonly LoadTestOptions defaultOptions;
    private readonly List<ConnectedLoadClient> connectedClients = new();
    private LoadTestCounters counters = new();
    private CancellationTokenSource? cancellation;
    private Task? workerTask;
    private DateTimeOffset? startedAt;
    private DateTimeOffset? stoppedAt;
    private string status = "Idle";
    private string errorMessage = "";

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
            this.counters = new LoadTestCounters();
            this.startedAt = DateTimeOffset.UtcNow;
            this.stoppedAt = null;
            this.status = "Starting";
            this.errorMessage = "";
            this.cancellation?.Dispose();
            this.cancellation = new CancellationTokenSource();
            LoadTestOptions options = this.CreateOptions(request);
            this.workerTask = Task.Run(() => this.RunAsync(options, this.cancellation.Token));
            return Task.CompletedTask;
        }
    }

    public async Task StopAsync()
    {
        Task? task;
        lock (this.syncRoot)
        {
            this.status = "Stopping";
            this.cancellation?.Cancel();
            task = this.workerTask;
        }

        if (task != null)
        {
            await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        }

        lock (this.syncRoot)
        {
            this.DisposeClients();
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
                    client.Client.IsConnected(),
                    $"{client.Client.GetIpAddress()}:{client.Client.GetPort()}"))
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

    private async Task RunAsync(LoadTestOptions options, CancellationToken cancellationToken)
    {
        try
        {
            for (int firstClientId = 1; firstClientId <= options.Clients && !cancellationToken.IsCancellationRequested; firstClientId += options.BatchSize)
            {
                int batchCount = Math.Min(options.BatchSize, options.Clients - firstClientId + 1);
                ClientAttemptResult[] results = await this.ConnectBatchAsync(options, firstClientId, batchCount);
                lock (this.syncRoot)
                {
                    foreach (ClientAttemptResult result in results)
                    {
                        if (result.Client != null)
                        {
                            result.Client.StartHealthCheckLoop();
                            this.connectedClients.Add(new ConnectedLoadClient(result.ClientId, result.Client));
                        }
                    }

                    this.status = "Holding";
                }

                if (options.RampDelayMilliseconds > 0)
                {
                    await Task.Delay(options.RampDelayMilliseconds, cancellationToken);
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
                this.status = "Failed";
                this.errorMessage = exception.Message;
            }
        }
    }

    private async Task<ClientAttemptResult[]> ConnectBatchAsync(LoadTestOptions options, int firstClientId, int batchCount)
    {
        Task<ClientAttemptResult>[] tasks = new Task<ClientAttemptResult>[batchCount];
        for (int index = 0; index < batchCount; index++)
        {
            int clientId = firstClientId + index;
            tasks[index] = Task.Run(() => this.ConnectClientAsync(options, clientId));
        }

        return await Task.WhenAll(tasks);
    }

    private async Task<ClientAttemptResult> ConnectClientAsync(LoadTestOptions options, int clientId)
    {
        Interlocked.Increment(ref this.counters.Attempted);
        TcpClient client = new(clientId, $"ui-client-{clientId}", options.Host, options.Port);
        try
        {
            bool connected = options.UseControlServer
                ? await client.ConnectViaControlServerAsync(options.Host, options.Port)
                : client.Connect();
            if (!connected)
            {
                Interlocked.Increment(ref this.counters.ConnectFail);
                client.Dispose();
                return ClientAttemptResult.Failed;
            }

            Interlocked.Increment(ref this.counters.Connected);
            if (!await SendAndReceiveHealthCheckAsync(client, options.HealthCheckTimeoutSeconds))
            {
                Interlocked.Increment(ref this.counters.HealthCheckFail);
                client.Dispose();
                return ClientAttemptResult.Failed;
            }

            Interlocked.Increment(ref this.counters.HealthCheckSuccess);
            return new ClientAttemptResult(clientId, client);
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
        {
            Interlocked.Increment(ref this.counters.HealthCheckFail);
            client.Dispose();
            return ClientAttemptResult.Failed;
        }
    }

    private LoadTestOptions CreateOptions(LoadTestUiStartRequest request)
    {
        int clients = Math.Max(1, request.Clients ?? this.defaultOptions.Clients);
        return this.defaultOptions with
        {
            Clients = clients,
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
        foreach (ConnectedLoadClient client in this.connectedClients)
        {
            client.Client.Dispose();
        }

        this.connectedClients.Clear();
    }

    private static async Task<bool> SendAndReceiveHealthCheckAsync(TcpClient client, int timeoutSeconds)
    {
        if (!await client.SendHealthCheckAsync())
        {
            return false;
        }

        Task<(bool Success, HealthCheckMessage Message)> receiveTask = client.TryReceiveHealthCheckAsync();
        Task completedTask = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        if (completedTask != receiveTask)
        {
            return false;
        }

        (bool success, HealthCheckMessage message) = await receiveTask;
        return success && message.Type == HealthCheckMessageType.Pong;
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

internal sealed class LoadTestUiStartRequest
{
    public int? Clients { get; init; }

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
