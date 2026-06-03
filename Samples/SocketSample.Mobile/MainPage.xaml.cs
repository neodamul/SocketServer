using System.Text.Json;
using SocketSample.Shared;

namespace SocketSample.Mobile;

public partial class MainPage : ContentPage
{
    private readonly SampleSocketClientSession session;

    public MainPage(SampleSocketClientSession session)
    {
        this.InitializeComponent();
        this.session = session;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await this.LoadInitialSettingsAsync();
        this.UpdateStatus();
    }

    private async Task LoadInitialSettingsAsync()
    {
        try
        {
            await using Stream stream = await FileSystem.OpenAppPackageFileAsync("config.json");
            SampleClientSettings? settings = await JsonSerializer.DeserializeAsync<SampleClientSettings>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (settings != null)
            {
                this.session.Configure(settings);
                this.ApplySettings(settings);
            }
        }
        catch (IOException)
        {
            this.ApplySettings(this.session.Settings);
        }
    }

    private void ApplySettings(SampleClientSettings settings)
    {
        this.ClientIdEntry.Text = settings.ClientId.ToString();
        this.ClientNameEntry.Text = settings.ClientName;
        this.HostEntry.Text = settings.Host;
        this.PortEntry.Text = settings.Port.ToString();
        this.ReceiveTimeoutEntry.Text = settings.ReceiveTimeoutSeconds.ToString();
        this.UseControlServerCheckBox.IsChecked = settings.UseControlServer;
    }

    private SampleClientSettings ReadSettings()
    {
        SampleClientSettings current = this.session.Settings;
        return new SampleClientSettings
        {
            ClientId = int.TryParse(this.ClientIdEntry.Text, out int clientId) ? clientId : current.ClientId,
            ClientName = string.IsNullOrWhiteSpace(this.ClientNameEntry.Text) ? current.ClientName : this.ClientNameEntry.Text.Trim(),
            Host = string.IsNullOrWhiteSpace(this.HostEntry.Text) ? current.Host : this.HostEntry.Text.Trim(),
            Port = int.TryParse(this.PortEntry.Text, out int port) ? port : current.Port,
            ReceiveTimeoutSeconds = int.TryParse(this.ReceiveTimeoutEntry.Text, out int timeout) ? timeout : current.ReceiveTimeoutSeconds,
            UseControlServer = this.UseControlServerCheckBox.IsChecked,
            Security = current.Security
        };
    }

    private void OnSaveClicked(object sender, EventArgs e)
    {
        this.session.Configure(this.ReadSettings());
        this.UpdateStatus();
    }

    private async void OnConnectClicked(object sender, EventArgs e)
    {
        this.session.Configure(this.ReadSettings());
        await this.RunAsync(() => this.session.ConnectAsync());
    }

    private async void OnRegisterClicked(object sender, EventArgs e)
    {
        await this.RunAsync(() => this.session.RegisterAsync());
    }

    private void OnDisconnectClicked(object sender, EventArgs e)
    {
        this.session.Disconnect();
        this.UpdateStatus();
    }

    private async void OnSendClicked(object sender, EventArgs e)
    {
        if (!uint.TryParse(this.TargetClientIdEntry.Text, out uint targetClientId))
        {
            await this.DisplayAlert("Send", "Target client id is required.", "OK");
            return;
        }

        await this.RunAsync(() => this.session.SendMessageAsync(targetClientId, this.MessageEntry.Text ?? ""));
    }

    private async void OnReceiveClicked(object sender, EventArgs e)
    {
        await this.RunAsync(async () => await this.session.ReceiveMessageAsync() != null);
    }

    private async Task RunAsync(Func<Task<bool>> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException exception)
        {
            await this.DisplayAlert("Socket", exception.Message, "OK");
        }
        finally
        {
            this.UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        SampleClientState state = this.session.GetState();
        this.StatusLabel.Text =
            $"Status: {state.Status}\n" +
            $"Connected: {state.IsConnected}\n" +
            $"Registered: {state.IsRegistered}\n" +
            $"Client: {state.ClientId}\n" +
            $"Endpoint: {state.Host}:{state.Port}\n" +
            $"Use ControlServer: {state.UseControlServer}\n" +
            $"Last Received: {state.LastReceivedMessage}\n" +
            $"Last Error: {state.LastError}";
    }
}
