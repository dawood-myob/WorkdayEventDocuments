namespace WorkdayEventDocuments;

public sealed class WorkdayOptions
{
    public string Host { get; set; } = "";
    public string Tenant { get; set; } = "";
    public string ApiVersion { get; set; } = "v47.0";
    public string OutputFolder { get; set; } = "Downloads";
    public string IntegrationSystemId { get; set; } = "";
    public int DaysBack { get; set; } = 30;
    public bool OnlyCompleted { get; set; } = true;
    public bool DownloadAllEventFiles { get; set; }
    public string VcfFolder { get; set; } = "VCF";
    public string VcfExtension { get; set; } = ".pgp";

    // Secrets: supply via environment variables (WORKDAY__CLIENTID, etc.), never appsettings.json.
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RefreshToken { get; set; } = "";

    // Basic auth (WS-Security UsernameToken). Workday expects the form username@tenant.
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseBasicAuth => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    public Uri SoapEndpoint => new($"https://{Host}/ccx/service/{Tenant}/Integrations/{ApiVersion}");
    public Uri TokenEndpoint => new($"https://{Host}/ccx/oauth2/{Tenant}/token");
}
