using Microsoft.Extensions.Configuration;
using WorkdayEventDocuments;

// Downloads the files of every event (and its parent event) of one integration, into a folder per run.

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets(typeof(WorkdayOptions).Assembly, optional: true)
    .AddEnvironmentVariables()
    .Build();

var options = config.GetSection("Workday").Get<WorkdayOptions>() ?? new WorkdayOptions();
Console.WriteLine($"SOAP endpoint : {options.SoapEndpoint}");
Console.WriteLine($"Token endpoint: {options.TokenEndpoint}");

var hasOAuth = !string.IsNullOrWhiteSpace(options.ClientId) && !string.IsNullOrWhiteSpace(options.ClientSecret) && !string.IsNullOrWhiteSpace(options.RefreshToken);
if (!options.UseBasicAuth && !hasOAuth)
{
    Console.Error.WriteLine("Set Workday:Username and Workday:Password in appsettings.json (or the OAuth ClientId/ClientSecret/RefreshToken).");
    return 1;
}

using var http = new HttpClient();
string? token = options.UseBasicAuth ? null : await new WorkdayAuth(http, options).GetAccessTokenAsync();
var client = new WorkdayIntegrationsClient(http, options);

try
{
    Console.WriteLine($"Integration   : {options.IntegrationSystemId} ({(options.DaysBack > 0 ? $"last {options.DaysBack} days" : "all history")}, {(options.OnlyCompleted ? "Completed only" : "all statuses")})");

    var all = await client.GetSystemEventsAsync(token, options.IntegrationSystemId, options.DaysBack > 0 ? DateTime.UtcNow.AddDays(-options.DaysBack) : null);
    var events = options.OnlyCompleted ? all.Where(e => e.Status == "Completed").ToList() : all.ToList();
    Console.WriteLine($"{all.Count} event(s) found, {events.Count} selected, {events.Count(e => e.ParentWid is not null)} with a parent event.");

    if (!options.DownloadAllEventFiles)
    {
        var vcfRoot = Path.GetFullPath(Path.Combine(options.OutputFolder, options.VcfFolder));
        Directory.CreateDirectory(vcfRoot);
        Console.WriteLine($"VCF folder    : {vcfRoot} (*{options.VcfExtension})");

        int saved = 0, existing = 0, missing = 0, noFile = 0;
        foreach (var ev in events.Where(e => e.Initiated is not null && e.ParentWid is not null).OrderBy(e => e.Initiated))
        {
            var siblings = await client.GetSiblingEventsAsync(token, ev);
            var vcf = siblings.SelectMany(s => s.Documents)
                .Where(d => d.FileName.EndsWith(options.VcfExtension, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(d => d.DocumentId).ToList();

            var dayFolder = Path.Combine(vcfRoot, ev.Initiated!.Value.LocalDateTime.ToString("yyyy-MM-dd"));
            if (vcf.Count == 0) { noFile++; Console.WriteLine($"{ev.ProcessId}: no {options.VcfExtension} file listed"); continue; }

            foreach (var d in vcf)
            {
                var path = Path.Combine(dayFolder, string.Concat(d.FileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)));
                if (File.Exists(path)) { existing++; continue; }
                try
                {
                    Directory.CreateDirectory(dayFolder);
                    var bytes = await client.DownloadDocumentAsync(token, d.DocumentId, path);
                    Console.WriteLine($"  saved {Path.GetRelativePath(vcfRoot, path)} ({bytes:N0} bytes)");
                    saved++;
                }
                catch (HttpRequestException ex)
                {
                    missing++;
                    File.Delete(path);
                    if (Directory.Exists(dayFolder) && !Directory.EnumerateFileSystemEntries(dayFolder).Any()) Directory.Delete(dayFolder);
                    Console.Error.WriteLine($"  skipped {d.FileName} ({ev.Initiated!.Value.LocalDateTime:yyyy-MM-dd}): HTTP {(int?)ex.StatusCode}");
                }
            }
        }

        Console.WriteLine($"Done. {saved} VCF file(s) saved, {existing} already present, {missing} not downloadable, {noFile} event(s) without a {options.VcfExtension} file.");
        return 0;
    }

    var runFolder = Path.GetFullPath(Path.Combine(options.OutputFolder, $"Run_{DateTime.Now:yyyyMMdd_HHmmss}"));
    Directory.CreateDirectory(runFolder);
    Console.WriteLine($"Run folder    : {runFolder}");

    int files = 0, failures = 0;
    foreach (var ev in events.OrderBy(e => e.Initiated))
    {
        var stamp = ev.Initiated?.LocalDateTime.ToString("yyyyMMdd_HHmmss") ?? "unknown";
        var eventFolder = Path.Combine(runFolder, $"{stamp}_{ev.Wid}");
        Console.WriteLine($"Event {ev.ProcessId ?? ev.Wid} [{ev.Status}] parent: {ev.ParentWid ?? "-"}, {ev.Documents.Count} document(s)");

        Directory.CreateDirectory(eventFolder);
        // The Integrations API cannot read the parent event (it is not an ESB invocation), so its ID is recorded instead.
        File.WriteAllLines(Path.Combine(eventFolder, "event_info.txt"),
        [
            $"Event WID        : {ev.Wid}",
            $"Event process ID : {ev.ProcessId}",
            $"Initiated        : {ev.Initiated:O}",
            $"Status           : {ev.Status}",
            $"Parent event WID : {ev.ParentWid}",
        ]);

        foreach (var d in ev.Documents)
        {
            try
            {
                var path = UniquePath(eventFolder, d.FileName, d.DocumentId);
                var bytes = await client.DownloadDocumentAsync(token, d.DocumentId, path);
                Console.WriteLine($"  saved {Path.GetRelativePath(runFolder, path)} ({bytes:N0} bytes)");
                files++;
            }
            catch (HttpRequestException ex)
            {
                failures++;
                Console.Error.WriteLine($"  skipped {d.FileName}: HTTP {(int?)ex.StatusCode} (not downloadable)");
            }
        }
    }

    Console.WriteLine($"Done. {files} file(s) saved, {failures} skipped (not downloadable from Workday).");
    return files > 0 || failures == 0 ? 0 : 3;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static string UniquePath(string folder, string fileName, string documentId)
{
    var name = string.Concat((string.IsNullOrWhiteSpace(fileName) ? documentId.Split('/').Last() : fileName)
        .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    var path = Path.Combine(folder, name);
    for (var i = 2; File.Exists(path); i++)
        path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)}_{i}{Path.GetExtension(name)}");
    return path;
}
