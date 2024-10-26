using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RestSharp;

class Program
{
    // API Credentials and Configuration
    static string authUrl = "https://posapi.at.tixly.com/v2/auth/token";
    static string clientId = Configuration.TixlyCliendID;
    static string clientSecret = Configuration.TixlyCliendSecretID;

    public static Dictionary<int, double> OrderPrice { get; set; } = new Dictionary<int, double>();
    public static Config Configuration { get; set; } = new Config();
    public static List<int> EtronIds = new List<int>
    {
        Configuration.Etron_ID_1,
        Configuration.Etron_ID_2,
        Configuration.Etron_ID_3,
        Configuration.Etron_ID_4,
        Configuration.Etron_ID_5,
        Configuration.Etron_ID_6,
        Configuration.Etron_ID_7,
        Configuration.Etron_ID_8,
        Configuration.Etron_ID_9,
        Configuration.Etron_ID_10,
        Configuration.Etron_ID_11
    };

    static List<int> previousTopOrders = new List<int>();

    static async Task Main(string[] args)
    {
        // Load configuration from JSON
        try
        {
            string configString = File.ReadAllText("config.json");
            Configuration = JsonSerializer.Deserialize<Config>(configString);
        }
        catch (Exception)
        {
            Configuration = new Config();
        }

        using (HttpClient client = new HttpClient())
        {
            var token = await Authenticate(client);
            if (string.IsNullOrEmpty(token))
            {
                Console.WriteLine("Failed to retrieve access token.");
                return;
            }

            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            string urlTemplate = "https://posapi.at.tixly.com/v2/order/get/{0}";
            int startingOrder = Configuration.TixlyId_OrderStart;

            while (true)
            {
                startingOrder -= 5;
                var (topOrders, lastProcessedOrder) = await GetTopOrders(client, urlTemplate, startingOrder);

                if (topOrders.Any())
                {
                    await TransferToEtron(topOrders);
                }
                else
                {
                    Console.WriteLine("No valid orders found.");
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }

    static async Task<string> Authenticate(HttpClient client)
    {
        var payload = new { ClientId = clientId, ClientSecret = clientSecret };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await client.PostAsync(authUrl, content);

        if (response.IsSuccessStatusCode)
        {
            var responseData = await response.Content.ReadAsStringAsync();
            var jsonResponse = JsonSerializer.Deserialize<AuthResponse>(responseData);
            return jsonResponse.Data.Token;
        }
        else
        {
            Console.WriteLine($"Authentication failed with status code {response.StatusCode}");
            return null;
        }
    }

    static async Task<(List<int> orders, int lastProcessedOrder)> GetTopOrders(HttpClient client, string urlTemplate, int startingOrder)
    {
        List<int> topOrders = new List<int>();
        int emptyCount = 0;
        Console.WriteLine($"GetTopOrders - Start at: {startingOrder}");

        for (int orderNumber = startingOrder; ; orderNumber++)
        {
            try
            {
                string url = string.Format(urlTemplate, orderNumber);
                var response = await client.GetAsync(url);
                string content = await response.Content.ReadAsStringAsync();
                var jsonResponse = JsonSerializer.Deserialize<JsonResponse>(content);

                if (jsonResponse.Status.Status == 0)
                {
                    topOrders.Add(orderNumber);
                    if (!OrderPrice.ContainsKey(orderNumber))
                    {
                        double totalAmount = OrderProcessor.GetTotalPaymentAmount(content);
                        OrderPrice[orderNumber] = totalAmount;
                    }
                    emptyCount = 0;
                }
                else
                {
                    emptyCount++;
                    if (emptyCount >= 20) break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching order number {orderNumber}: {ex.Message}");
            }
        }

        return (topOrders, topOrders.Count > 0 ? topOrders.Last() : startingOrder);
    }

    static async Task TransferToEtron(List<int> topOrders)
    {
        var uniqueOrders = new HashSet<int>(previousTopOrders);
        uniqueOrders.UnionWith(topOrders);

        var top10UniqueOrders = uniqueOrders.OrderByDescending(o => o).Take(10).ToList();

        Console.WriteLine("Transferring the following top 10 unique orders to Etron:");
        foreach (var item in top10UniqueOrders)
        {
            Console.WriteLine(item);
        }

        var client = new RestClient(Configuration.EtronAdress);

        for (int i = 0; i < top10UniqueOrders.Count; i++)
        {
            var orderNumber = top10UniqueOrders[i];
            var nameRequest = new RestRequest($"api/v2/write/product.template/?ids={EtronIds[i]}&values={{ \"name\": \"{Configuration.Prefix}: {orderNumber}\" }}", Method.Put);
            nameRequest.AddHeader("Authorization", Configuration.EtronAuthorization);
            nameRequest.AddHeader("Cookie", Configuration.EtronCookie);

            var priceRequest = new RestRequest($"api/v2/write/product.template/?ids={EtronIds[i]}&values={{ \"list_price\": \"{OrderPrice[orderNumber]}\" }}", Method.Put);
            priceRequest.AddHeader("Authorization", Configuration.EtronAuthorization);
            priceRequest.AddHeader("Cookie", Configuration.EtronCookie);

            await client.ExecuteAsync(nameRequest);
            await client.ExecuteAsync(priceRequest);
        }

        previousTopOrders = top10UniqueOrders;
    }
}

public class AuthResponse
{
    public AuthData Data { get; set; }
}

public class AuthData
{
    public string Token { get; set; }
}

public class JsonResponse
{
    public JsonStatus Status { get; set; }
    public JsonData? Data { get; set; }
}

public class JsonStatus
{
    public int Status { get; set; }
}

public class JsonData
{
    public object OrderId { get; set; }
}

public static class OrderProcessor
{
    public static double GetTotalPaymentAmount(string jsonResponse)
    {
        using (JsonDocument doc = JsonDocument.Parse(jsonResponse))
        {
            JsonElement payments = doc.RootElement.GetProperty("Data").GetProperty("Payments");
            if (payments.GetArrayLength() > 0)
            {
                return payments[0].GetProperty("Amount").GetDouble();
            }
        }
        return 0.0;
    }
}

public class Config
{
    public string Prefix { get; set; } = "Tixly_ID_Test:";
    public int TixlyId_OrderStart { get; set; } = 20000;
    public int Etron_ID_1 { get; set; } = 145;
    public int Etron_ID_2 { get; set; } = 146;
    public int Etron_ID_3 { get; set; } = 147;
    public int Etron_ID_4 { get; set; } = 148;
    public int Etron_ID_5 { get; set; } = 149;
    public int Etron_ID_6 { get; set; } = 150;
    public int Etron_ID_7 { get; set; } = 151;
    public int Etron_ID_8 { get; set; } = 152;
    public int Etron_ID_9 { get; set; } = 153;
    public int Etron_ID_10 { get; set; } = 154;
    public int Etron_ID_11 { get; set; } = 155;
    public string EtronAuthorization { get; set; }
    public string EtronCookie { get; set; }
    public string TixlyCliendID { get; set; }
    public string TixlyCliendSecretID { get; set; }
    public string EtronAdress { get; set; }
    public string LogFile { get; set; }
}
