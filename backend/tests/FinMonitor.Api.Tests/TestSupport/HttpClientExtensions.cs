using System.Net.Http.Json;
using System.Text.Json;

namespace FinMonitor.Api.Tests.TestSupport;

internal static class HttpClientExtensions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task<HttpResponseMessage> PostTransactionAsync(this HttpClient client, TransactionPayload payload) =>
        client.PostAsJsonAsync("/api/transactions", payload, Json);

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(Json);

        Assert.NotNull(value);
        return value;
    }
}
