using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WorkdayEventDocuments;

public sealed class WorkdayAuth(HttpClient http, WorkdayOptions options)
{
    // Exchanges the long-lived refresh token (from an "API Client for Integrations") for a bearer access token.
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = options.RefreshToken
            })
        };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.ClientId}:{options.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Token request failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Token response had no access_token.");
    }
}
