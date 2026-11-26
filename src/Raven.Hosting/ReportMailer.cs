using System.Net.Http.Json;
using System.Text.Json;

namespace Raven.Hosting;

/// <summary>
/// Who hears of a new report (README, "Report mail"): RavenHosting:ReportMail:ApiKey, an SMTP2GO API key allowed
/// /email/send; :From, a sender on a domain SMTP2GO has verified; :To, one or more addresses. Off while the key or the
/// recipients are missing.
/// </summary>
public class ReportMailOptions
{
    public string ApiKey { get; init; } = "";
    public string From { get; init; } = "";
    public IReadOnlyList<string> To { get; init; } = [];

    public bool Enabled => ApiKey.Length > 0 && To.Count > 0;

    public static ReportMailOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("RavenHosting:ReportMail");
        var options = new ReportMailOptions
        {
            ApiKey = section["ApiKey"]?.Trim() ?? "",
            From = section["From"]?.Trim() ?? "",
            To = (section.GetSection("To").Get<string[]>() ?? [])
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList(),
        };
        if (options.Enabled && options.From.Length == 0)
        {
            throw new InvalidOperationException("RavenHosting:ReportMail:From is not configured");
        }
        return options;
    }
}

/// <summary>
/// Mails a bare notice that a report was filed: its number, when, and where admins read it. Never what the person
/// reporting wrote, so their contact details and evidence stay in the database. Sent through SMTP2GO's HTTPS API, as
/// odin-core sends its own mail (Smtp2GoApi, Smtp2GoEmailSender).
/// </summary>
public class ReportMailer(ReportMailOptions options, UpgradeOptions site, IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "report-mail";
    private const string SendUrl = "https://api.smtp2go.com/v3/email/send";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>
    /// Does nothing while report mail is off. Throws when SMTP2GO refuses it; the caller logs that, the report being
    /// stored already. Only called from /report, which is off without the site, so the site's domain is set.
    /// </summary>
    public async Task SendAsync(long reportId, long filedAtMs)
    {
        if (!options.Enabled)
        {
            return;
        }

        var domain = site.PublicDomain;
        var body = new SendRequest(
            options.From,
            options.To,
            $"Report #{reportId} filed on {domain}",
            $"""
             Report #{reportId} was filed on {domain} at {AdminForm.When(filedAtMs)}.

             Read it at https://{domain}{AdminPages.Root}/reports
             """);

        using var request = new HttpRequestMessage(HttpMethod.Post, SendUrl);
        // A header, as odin-core sends it, so the key is never in a URL or a body
        request.Headers.Add("X-Smtp2go-Api-Key", options.ApiKey);
        request.Content = JsonContent.Create(body, options: Json);

        using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        var data = Read(content);

        if (!response.IsSuccessStatusCode)
        {
            // Their errors come as a 4xx with the detail in data.error; the status alone says little
            throw new InvalidOperationException(
                $"SMTP2GO /email/send returned {(int)response.StatusCode}: {data?.Error ?? content}");
        }

        // A 200 can still refuse recipients. Only that is a failure, and it is not retried: a mail that went out but
        // read back oddly must not be sent twice
        if (data?.Failed > 0)
        {
            throw new InvalidOperationException(
                $"SMTP2GO /email/send accepted {data.Succeeded} recipient(s) and refused {data.Failed}");
        }
    }

    private static ResponseData? Read(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<Response>(content, Json)?.Data;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private record SendRequest(string Sender, IReadOnlyList<string> To, string Subject, string TextBody);

    private record Response(ResponseData? Data);

    private record ResponseData(int Succeeded, int Failed, string? Error);
}
