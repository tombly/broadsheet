using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

namespace Broadsheet.Telemetry;

/// <summary>
/// Reads Azure Monitor workbooks over ARM: lists the ones an identity can see, and fetches one
/// with its content so <see cref="WorkbookParser"/> can turn it into a board.
/// </summary>
public sealed class WorkbookClient(TokenCredential? credential = null)
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string ArmRoot = "https://management.azure.com";
    private const string ApiVersion = "2022-04-01";

    private static readonly HttpClient _httpClient = new();

    private readonly TokenCredential _credential = credential ?? new DefaultAzureCredential();

    /// <summary>Renderable workbooks visible in the given subscriptions, ordered by display name.</summary>
    public async Task<IReadOnlyList<BoardDescriptor>> List(
        IEnumerable<string> subscriptionIds, CancellationToken ct = default)
    {
        var boards = new List<BoardDescriptor>();
        foreach (var subscription in subscriptionIds)
        {
            var url = $"{ArmRoot}/subscriptions/{subscription}/providers/microsoft.insights/workbooks"
                + $"?api-version={ApiVersion}&category=workbook&canFetchContent=true";

            var payload = await Get(url, ct);
            foreach (var workbook in payload["value"]?.AsArray() ?? [])
            {
                var id = workbook?["id"]?.GetValue<string>();
                var properties = workbook?["properties"];
                var name = properties?["displayName"]?.GetValue<string>();

                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name) && ChartCount(properties) > 0)
                    boards.Add(new BoardDescriptor(name, id));
            }
        }

        return boards.OrderBy(board => board.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<WorkbookModel> Load(string workbookResourceId, CancellationToken ct = default)
    {
        var url = $"{ArmRoot}{workbookResourceId}?api-version={ApiVersion}&canFetchContent=true";
        var properties = (await Get(url, ct))["properties"]
            ?? throw new InvalidOperationException("Workbook response carried no properties.");

        var serialized = properties["serializedData"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Workbook response carried no serializedData.");

        return WorkbookParser.Parse(
            properties["displayName"]?.GetValue<string>() ?? "Workbook",
            properties["sourceId"]?.GetValue<string>() ?? string.Empty,
            serialized);
    }

    /// <summary>
    /// How many charts a listed workbook would render. Listing asks for content so a workbook that
    /// draws nothing - prose only, or queries this cannot run - stays out of the picker instead of
    /// offering a board with no tiles. Content that will not parse renders nothing either.
    /// </summary>
    private static int ChartCount(JsonNode? properties)
    {
        var serialized = properties?["serializedData"]?.GetValue<string>();
        if (string.IsNullOrEmpty(serialized))
            return 0;

        try
        {
            return WorkbookParser.Parse(string.Empty, string.Empty, serialized).ChartCount;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private async Task<JsonNode> Get(string url, CancellationToken ct)
    {
        AccessToken token;
        try
        {
            token = await _credential.GetTokenAsync(new TokenRequestContext([ArmScope]), ct);
        }
        catch (CredentialUnavailableException ex)
        {
            throw new TelemetryAuthException("No Azure credential available. Run `az login`.", ex);
        }
        catch (AuthenticationFailedException ex)
        {
            throw new TelemetryAuthException("Azure sign-in expired or failed. Run `az login` and retry.", ex);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new TelemetryAuthException("Signed-in identity cannot read this workbook (need Reader on its resource group).");

        response.EnsureSuccessStatusCode();

        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))
            ?? throw new InvalidOperationException("Workbook response was not JSON.");
    }
}
