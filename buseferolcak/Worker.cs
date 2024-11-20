using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using static System.Runtime.InteropServices.JavaScript.JSType;


namespace buseferolcak
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly HttpClient _httpClient;
        private readonly string username = "apitest";
        private readonly string password = "test123";
        private string _token = "";

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
            _httpClient = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            });
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);

                await GetTokenAsync();
                await GetDataAsync();

                // Bekleme süresi (örneðin, 10 saniye)
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        private async Task GetTokenAsync()
        {
            var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);

            var response = await _httpClient.PostAsync("https://efatura.etrsoft.com/fmi/data/v1/databases/testdb/sessions", new StringContent("{}", Encoding.UTF8, "application/json"));
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var jsonDocument = JsonDocument.Parse(content);
                _token = jsonDocument.RootElement.GetProperty("response").GetProperty("token").GetString();
                _logger.LogInformation("Token alýndý: {token}", _token);
            }
            else
            {
                _logger.LogError("Token alma iþlemi baþarýsýz oldu: {statusCode}", response.StatusCode);
            }
        }

        private async Task GetDataAsync()
        {
            if (string.IsNullOrEmpty(_token))
            {
                _logger.LogError("Token bulunamadý. Veri çekme iþlemi atlanýyor.");
                return;
            }

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            var bodyContent = new StringContent(JsonSerializer.Serialize(new { fieldData = new { }, script = "getData" }), Encoding.UTF8, "application/json");
            var response = await _httpClient.PatchAsync("https://efatura.etrsoft.com/fmi/data/v1/databases/testdb/layouts/testdb/records/1", bodyContent);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                _logger.LogInformation("Gelen JSON verisi: {jsonContent}", content);

                // JSON verisini ayýklama
                var relevantData = ExtractRelevantData(content);

                // Ayýklanan veriyi SQL'e kaydetme
                await SaveDataToDatabaseAsync(relevantData);
            }
            else
            {
                _logger.LogError("Veri çekme iþlemi baþarýsýz oldu: {statusCode}", response.StatusCode);
            }
        }

        private List<(string HesapKodu, decimal ToplamBorc)> ExtractRelevantData(string jsonData)
        {
            var results = new List<(string HesapKodu, decimal ToplamBorc)>();

            try
            {
                using (JsonDocument document = JsonDocument.Parse(jsonData))
                {
                    var root = document.RootElement;

                    if (root.TryGetProperty("response", out var responseElement) &&
                        responseElement.TryGetProperty("scriptResult", out var scriptResultElement))
                    {
                        if (scriptResultElement.ValueKind == JsonValueKind.Array)
                        {
                            // JSON dizisi olarak iþleme
                            foreach (var record in scriptResultElement.EnumerateArray())
                            {
                                AddRecordToResults(results, record);
                            }
                        }
                        else if (scriptResultElement.ValueKind == JsonValueKind.String)
                        {
                            // JSON string olarak iþleme
                            var jsonArrayString = scriptResultElement.GetString();
                            using (JsonDocument arrayDocument = JsonDocument.Parse(jsonArrayString))
                            {
                                var arrayRoot = arrayDocument.RootElement;

                                if (arrayRoot.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var record in arrayRoot.EnumerateArray())
                                    {
                                        AddRecordToResults(results, record);
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning("ScriptResult beklenen JSON dizisi yerine {type} türünde.", arrayRoot.ValueKind);
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning("ScriptResult beklenen JSON dizisi veya string yerine {type} türünde.", scriptResultElement.ValueKind);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Response veya ScriptResult baþlýklarý JSON'da bulunamadý.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("JSON iþleme sýrasýnda hata oluþtu: {message}", ex.Message);
            }

            return results;
        }


        private void AddRecordToResults(List<(string HesapKodu, decimal ToplamBorc)> results, JsonElement record)
        {
            if (record.TryGetProperty("hesap_kodu", out var hesapKoduProp) &&
       record.TryGetProperty("borc", out var borcProp))
            {
                var hesapKodu = hesapKoduProp.GetString()?.Trim();

                decimal toplamBorc = 0;

                // Borc deðeri string veya number olarak geldiðinde
                if (borcProp.ValueKind == JsonValueKind.String)
                {
                    var borcString = borcProp.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(borcString))
                    {
                        if (decimal.TryParse(borcString, NumberStyles.Any, CultureInfo.InvariantCulture, out toplamBorc))
                        {
                            // Geçerli borc deðeri
                            _logger.LogInformation("Geçerli borc deðeri alýndý: HesapKodu: {HesapKodu}, Borc: {ToplamBorc}", hesapKodu, toplamBorc);
                        }
                        else
                        {
                            _logger.LogWarning("Borc deðeri geçersiz. HesapKodu: {HesapKodu}, Borc: {BorcString}", hesapKodu, borcString);
                        }
                    }
                }
                else if (borcProp.ValueKind == JsonValueKind.Number)
                {
                    toplamBorc = borcProp.GetDecimal();
                    // Geçerli borc deðeri
                    _logger.LogInformation("Geçerli borc deðeri alýndý: HesapKodu: {HesapKodu}, Borc: {ToplamBorc}", hesapKodu, toplamBorc);
                }

                if (!string.IsNullOrEmpty(hesapKodu) && toplamBorc >= 0)
                {
                    results.Add((hesapKodu, toplamBorc));
                }
                else
                {
                    _logger.LogWarning("Geçersiz veri: HesapKodu veya ToplamBorc boþ veya geçersiz. HesapKodu: {HesapKodu}, Borc: {ToplamBorc}", hesapKodu, toplamBorc);
                }
            }
        }



        private async Task SaveDataToDatabaseAsync(IEnumerable<(string HesapKodu, decimal ToplamBorc)> data)
        {
            string connectionstring = "Server=localhost;Database=api;Uid=root;Pwd=1234;";
            using (var connection = new MySqlConnection(connectionstring))
            {
                await connection.OpenAsync();

                foreach (var (hesapKodu, toplamBorc) in data)
                {
                    string query = @"INSERT INTO datarecord (HesapKodu, ToplamBorc) 
                             VALUES (@HesapKodu, @ToplamBorc) 
                             ON DUPLICATE KEY UPDATE ToplamBorc = VALUES(ToplamBorc)";

                    using (var command = new MySqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@HesapKodu", hesapKodu);
                        command.Parameters.AddWithValue("@ToplamBorc", toplamBorc);

                        await command.ExecuteNonQueryAsync();
                    }
                }
            }
        }

    }
}
