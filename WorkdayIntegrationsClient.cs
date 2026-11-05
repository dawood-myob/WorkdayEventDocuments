using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WorkdayEventDocuments;

public sealed record EventDocument(string DocumentId, string FileName, long? FileSize, string? ContentType);

public sealed record IntegrationEvent(string Id, string IdType, string? Descriptor);

public sealed record EventInfo(string Wid, string? ProcessId, DateTimeOffset? Initiated, string? Status, string? ParentWid, IReadOnlyList<EventDocument> Documents);

public sealed class WorkdayIntegrationsClient(HttpClient http, WorkdayOptions options)
{
    private static readonly XNamespace Wd = "urn:com.workday/bsvc";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private static readonly string PasswordType = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordText";

    // accessToken null => use WS-Security username/password from options.
    public async Task<IReadOnlyList<IntegrationEvent>> GetRecentEventsAsync(
        string? accessToken, int count = 5, CancellationToken ct = default)
    {
        var body = await SendAsync(accessToken,
            new XElement(Wd + "Get_Integration_Events_Request",
                new XAttribute(Wd + "version", options.ApiVersion),
                new XElement(Wd + "Response_Filter",
                    new XElement(Wd + "Page", 1),
                    new XElement(Wd + "Count", count))), ct);

        return body.Descendants(Wd + "Integration_Event")
            .Select(e => e.Element(Wd + "Integration_Event_Reference"))
            .Where(r => r is not null)
            .Select(r =>
            {
                var id = r!.Elements(Wd + "ID").FirstOrDefault(i => (string?)i.Attribute(Wd + "type") == "WID")
                         ?? r.Elements(Wd + "ID").FirstOrDefault();
                return new IntegrationEvent(id?.Value ?? "", (string?)id?.Attribute(Wd + "type") ?? "", (string?)r.Attribute(Wd + "Descriptor"));
            })
            .ToList();
    }

    // Lists every event of one integration system since a date, following all result pages.
    public Task<IReadOnlyList<EventInfo>> GetSystemEventsAsync(
        string? accessToken, string integrationSystemId, DateTime? sentAfterUtc, CancellationToken ct = default) =>
        QueryEventsAsync(accessToken,
        [
            new XElement(Wd + "Integration_System_Reference",
                new XElement(Wd + "ID", new XAttribute(Wd + "type", "Integration_System_ID"), integrationSystemId)),
            sentAfterUtc is null ? null : new XElement(Wd + "Sent_After", XmlConvert.ToString(sentAfterUtc.Value, XmlDateTimeSerializationMode.Utc)),
        ], ct);

    // The VCF .pgp files are attached to the "Document Retrieval" event that shares the parent (and start time) of the integration event.
    public async Task<IReadOnlyList<EventInfo>> GetSiblingEventsAsync(string? accessToken, EventInfo ev, CancellationToken ct = default)
    {
        var start = ev.Initiated!.Value.UtcDateTime;
        var window = await QueryEventsAsync(accessToken,
        [
            new XElement(Wd + "Sent_After", XmlConvert.ToString(start.AddMinutes(-1), XmlDateTimeSerializationMode.Utc)),
            new XElement(Wd + "Sent_Before", XmlConvert.ToString(start.AddMinutes(1), XmlDateTimeSerializationMode.Utc)),
        ], ct);
        return window.Where(e => e.Wid != ev.Wid && e.ParentWid == ev.ParentWid).ToList();
    }

    private async Task<IReadOnlyList<EventInfo>> QueryEventsAsync(string? accessToken, XElement?[] criteria, CancellationToken ct)
    {
        var events = new List<EventInfo>();
        int page = 1, totalPages = 1;
        do
        {
            var body = await SendAsync(accessToken,
                new XElement(Wd + "Get_Integration_Events_Request",
                    new XAttribute(Wd + "version", options.ApiVersion),
                    new XElement(Wd + "Request_Criteria", criteria),
                    new XElement(Wd + "Response_Filter",
                        new XElement(Wd + "Page", page),
                        new XElement(Wd + "Count", 100))), ct);

            totalPages = (int?)body.Descendants(Wd + "Response_Results").Elements(Wd + "Total_Pages").FirstOrDefault() ?? 1;

            foreach (var e in body.Descendants(Wd + "Integration_Event"))
            {
                var wid = WidOf(e.Element(Wd + "Integration_Event_Reference"));
                if (wid is null) continue;

                var initiated = (DateTimeOffset?)e.Element(Wd + "Integration_Event_Data")?.Element(Wd + "Initiated_DateTime");
                var process = e.Element(Wd + "Background_Process_Instance_Data");
                var status = process?.Element(Wd + "Background_Process_Instance_Status_Reference")?.Elements(Wd + "ID")
                    .FirstOrDefault(i => (string?)i.Attribute(Wd + "type") == "Background_Process_Instance_Status_ID")?.Value;
                var processId = e.Element(Wd + "Integration_Event_Reference")?.Elements(Wd + "ID")
                    .FirstOrDefault(i => (string?)i.Attribute(Wd + "type") == "Background_Process_Instance_ID")?.Value;

                // Output documents are listed on the event itself, so no extra Get_Event_Documents call is needed.
                var docs = e.Descendants(Wd + "Integration_Repository_Document_Data")
                    .Select(d => new EventDocument(
                        (string?)d.Attribute(Wd + "Document_ID") ?? "",
                        (string?)d.Attribute(Wd + "File_Name") ?? "",
                        (long?)d.Element(Wd + "File_Size"),
                        d.Element(Wd + "Content_Type_Reference")?.Elements(Wd + "ID")
                            .FirstOrDefault(i => (string?)i.Attribute(Wd + "type") == "Content_Type_ID")?.Value))
                    .Where(d => d.DocumentId.Length > 0)
                    .ToList();

                events.Add(new EventInfo(wid, processId, initiated, status, WidOf(process?.Element(Wd + "Parent_Event_Reference")), docs));
            }
            page++;
        } while (page <= totalPages);

        return events;
    }

    private static string? WidOf(XElement? reference) =>
        reference?.Elements(Wd + "ID").FirstOrDefault(i => (string?)i.Attribute(Wd + "type") == "WID")?.Value;

    // SOAP only returns document metadata; the bytes come from the cc-blobitory endpoint.
    public async Task<long> DownloadDocumentAsync(string? accessToken, string documentId, string destinationPath, CancellationToken ct = default)
    {
        var path = string.Join('/', documentId.Split('/').Select(Uri.EscapeDataString));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://{options.Host}/ccx/cc-blobitory/{options.Tenant}/{path}"));
        request.Headers.Authorization = accessToken is not null
            ? new AuthenticationHeaderValue("Bearer", accessToken)
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}")));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Download failed ({(int)response.StatusCode}) for {documentId}", null, response.StatusCode);

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(destinationPath);
        await source.CopyToAsync(target, ct);
        return target.Length;
    }

    public async Task<IReadOnlyList<EventDocument>> GetEventDocumentsAsync(
        string? accessToken, string eventId, string idType = "WID", CancellationToken ct = default)
    {
        var body = await SendAsync(accessToken,
            new XElement(Wd + "Get_Event_Documents_Request",
                new XAttribute(Wd + "version", options.ApiVersion),
                new XElement(Wd + "Request_References",
                    new XElement(Wd + "Integration_Event_Reference",
                        new XElement(Wd + "ID",
                            new XAttribute(Wd + "type", idType),
                            eventId)))), ct);

        return body.Descendants(Wd + "Repository_Document_Data")
            .Select(d => new EventDocument(
                (string?)d.Attribute(Wd + "Document_ID") ?? "",
                (string?)d.Attribute(Wd + "File_Name") ?? "",
                (long?)d.Element(Wd + "File_Size"),
                (string?)d.Element(Wd + "Content_Type")))
            .ToList();
    }

    private async Task<XDocument> SendAsync(string? accessToken, XElement bodyContent, CancellationToken ct)
    {
        var envelope = new XDocument(
            new XElement(Soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soapenv", Soap),
                new XAttribute(XNamespace.Xmlns + "bsvc", Wd),
                accessToken is not null ? null : new XElement(Soap + "Header",
                    new XElement(Wsse + "Security",
                        new XAttribute(Soap + "mustUnderstand", "1"),
                        new XElement(Wsse + "UsernameToken",
                            new XElement(Wsse + "Username", options.Username),
                            new XElement(Wsse + "Password", new XAttribute("Type", PasswordType), options.Password)))),
                new XElement(Soap + "Body", bodyContent)));

        using var request = new HttpRequestMessage(HttpMethod.Post, options.SoapEndpoint)
        {
            Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
        };
        if (accessToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("SOAPAction", "\"\"");

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"SOAP call failed ({(int)response.StatusCode}): {body}");

        return XDocument.Parse(body);
    }
}
