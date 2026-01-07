using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.IO;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace FusionComms.Controllers
{
    [AllowAnonymous]
    [Route("flows")]
    [ApiController]
    public class FlowsController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<FlowsController> _logger;
        private readonly IConfiguration _configuration;

        public FlowsController(IHttpClientFactory httpClientFactory, ILogger<FlowsController> logger, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _configuration = configuration;
        }

        [HttpPost("endpoint")]
        public async Task<IActionResult> PostEndpoint([FromBody] FlowEncryptedRequest req)
        {
            _logger.LogInformation("🚀 FLOW HIT {Path} {Method} from {Remote}",
                Request?.Path, Request?.Method,
                Request?.Headers.ContainsKey("X-Forwarded-For") == true ? Request.Headers["X-Forwarded-For"].ToString() : HttpContext?.Connection?.RemoteIpAddress?.ToString());

            _logger.LogDebug("Request headers: Host={Host}, Content-Length={Length}",
                Request?.Headers["Host"].ToString(), Request?.ContentLength);

            _logger.LogDebug("Encrypted payload sizes: data={DataLen}, key={KeyLen}, iv={IvLen}",
                req?.encrypted_flow_data?.Length ?? 0,
                req?.encrypted_aes_key?.Length ?? 0,
                req?.initial_vector?.Length ?? 0);

            try
            {
                var privatePem = GetPrivateKeyPem(_configuration);

                using var rsa = RSA.Create();
                rsa.ImportFromPem(privatePem);

                var decryptedJson = DecryptFlowRequest(req, rsa, out var aesKey, out var iv);
                _logger.LogInformation("🔓 Decrypted payload succeeded (len={Len})", decryptedJson?.Length ?? 0);
                _logger.LogDebug("Decrypted payload content: {Decrypted}", decryptedJson);

                // parse action from decrypted payload
                string action = null;
                try
                {
                    using var doc = JsonDocument.Parse(decryptedJson ?? "{}");
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("action", out var aProp))
                        action = aProp.GetString();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse decrypted payload JSON");
                }

                object response;

                using var doc2 = JsonDocument.Parse(decryptedJson ?? "{}");
                var screen = doc2.RootElement.ValueKind == JsonValueKind.Object && doc2.RootElement.TryGetProperty("screen", out var sProp) ? sProp.GetString() : null;

                _logger.LogInformation("➡️ Flow action: {Action}, screen: {Screen}", action, screen);

                // Consider navigate to screen_igvcep OR any navigate that carries passed_address
                var canCalculateFee = false;
                JsonElement dataElement = default;
                if (doc2.RootElement.TryGetProperty("data", out var tmpData))
                    dataElement = tmpData;

                var hasAddress = dataElement.ValueKind == JsonValueKind.Object && dataElement.TryGetProperty("passed_address", out _);
                if (string.Equals(action, "navigate", StringComparison.OrdinalIgnoreCase) && (string.Equals(screen, "screen_igvcep", StringComparison.OrdinalIgnoreCase) || hasAddress))
                {
                    // extract user-entered address and phone from the payload (if present)
                    var data = dataElement.ValueKind == JsonValueKind.Object ? dataElement : new JsonElement();
                    var address = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("passed_address", out var a) ? a.GetString() ?? string.Empty : string.Empty;
                    var phone = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("passed_phone", out var p) ? p.GetString() ?? string.Empty : string.Empty;

                    _logger.LogInformation("📍 Calculating delivery fee for address: {Address}; phone: {Phone}; screen: {Screen}", address, phone, screen);

                    var client = _httpClientFactory.CreateClient();
                    // Demo bearer token provided by user (remove or move to configuration in production)
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJuYW1laWQiOiI0MTZhYjA2My04ZmViLTQ0MmItYWM1Zi0zYTM0ZjA1NThmMzciLCJuYW1lIjoiU2V5aWZ1bm1pIE9sYWZpb3llIiwicm9sZSI6Ik1hbmFnZXIiLCJ1c2VyTmFtZSI6I");

                    var feeRequest = new DeliveryFeeRequest
                    {
                        source_address_string = "Panarotics",
                        destination_address_string = address ?? string.Empty,
                        estimated_order_amount = 0
                    };

                    var feeUrl = "https://api.food-ease.io/api/v1/Chowdeck/get-delivery-fee";
                    var feeReqJson = JsonSerializer.Serialize(feeRequest);
                    _logger.LogInformation("➡️ POST {Url} with body: {Body}", feeUrl, feeReqJson);

                    var content = new StringContent(feeReqJson, Encoding.UTF8, "application/json");
                    var httpResponse = await client.PostAsync(feeUrl, content);

                    var respText = await httpResponse.Content.ReadAsStringAsync();
                    _logger.LogInformation("⬅️ Delivery fee API response: {Status} {Body}", (int)httpResponse.StatusCode, respText);

                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        _logger.LogError("❌ Delivery fee API failed (status: {Status}). Body: {Body}", (int)httpResponse.StatusCode, respText);
                        throw new Exception("Delivery fee API call failed: " + respText);
                    }

                    DeliveryFeeResponse feeResponse = null;
                    try
                    {
                        feeResponse = JsonSerializer.Deserialize<DeliveryFeeResponse>(respText);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to deserialize delivery fee response JSON; raw body: {Body}", respText);
                    }

                    var totalAmount = feeResponse?.data?.total_amount ?? 0L;
                    _logger.LogInformation("💰 Delivery fee resolved: {Amount}", totalAmount);

                    response = new
                    {
                        version = "3.0",
                        screen = "screen_igvcep",
                        data = new
                        {
                            passed_address = address,
                            passed_phone = phone,
                            delivery_price = totalAmount.ToString()
                        }
                    };
                }
                else
                {
                    // default: load the address entry screen (screen 1) with active status
                    response = new
                    {
                        version = "3.0",
                        screen = "screen_asnlyt",
                        data = new
                        {
                            status = "active"
                        }
                    };
                }

                var flowJson = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
                _logger.LogDebug("📦 FLOW JSON SENT TO WHATSAPP (before encryption): {flowJson}", flowJson);

                var encrypted = EncryptFlowResponse(response, aesKey, iv);

                _logger.LogInformation("✅ FLOW RESPONSE OK");
                return Content(encrypted, "application/json");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🔥 FLOW ERROR");
                return StatusCode(500);
            }
        }

        // DTOs
        public sealed record FlowEncryptedRequest(
            string encrypted_flow_data,
            string encrypted_aes_key,
            string initial_vector
        );

        public sealed class ExternalArea
        {
            public string id { get; set; } = string.Empty;
            public string title { get; set; } = string.Empty;
        }

        // DTOs for external API
        public class ExternalApiResponse
        {
            public DataContainer data { get; set; } = new DataContainer();
        }

        public class DataContainer
        {
            public List<ChargeItem> data { get; set; } = new List<ChargeItem>();
        }

        public class ChargeItem
        {
            public string id { get; set; } = "";
            public string title { get; set; } = "";
            public List<ChargeService> chargeServices { get; set; } = new List<ChargeService>();
        }

        public class ChargeService
        {
            public string orderCharge { get; set; } = "";
        }

        // DTOs for delivery fee API
        public sealed class DeliveryFeeRequest
        {
            public string source_address_string { get; set; } = "Panarotics";
            public string destination_address_string { get; set; } = "";
            public int estimated_order_amount { get; set; } = 0;
            public Coordinate source_address { get; set; } = new Coordinate();
            public Coordinate destination_address { get; set; } = new Coordinate();
        }

        public sealed class Coordinate
        {
            public double latitude { get; set; } = 0;
            public double longitude { get; set; } = 0;
        }

        public sealed class DeliveryFeeResponse
        {
            public DeliveryFeeData data { get; set; } = new DeliveryFeeData();
            public List<string> errors { get; set; } = new List<string>();
        }

        public sealed class DeliveryFeeData
        {
            public long total_amount { get; set; }
        }

        // Crypto helpers
        private static byte[] FlipIv(byte[] iv)
        {
            var flipped = (byte[])iv.Clone();
            for (int i = 0; i < flipped.Length; i++)
                flipped[i] ^= 0xFF;
            return flipped;
        }

        private static string GetPrivateKeyPem(IConfiguration config)
        {
            // Try direct PEM env var
            var pem = config["PRIVATE_KEY_PEM"] ?? Environment.GetEnvironmentVariable("PRIVATE_KEY_PEM");

            // If pem looks like it's just the BEGIN line (common when .env parsing is naive),
            // or if it's a short single-line value, try the base64 alternative.
            if (string.IsNullOrWhiteSpace(pem) || !pem.Contains("-----BEGIN") || !pem.Contains("-----END"))
            {
                // Try base64 encoded pem
                var pemB64 = config["PRIVATE_KEY_PEM_B64"] ?? Environment.GetEnvironmentVariable("PRIVATE_KEY_PEM_B64");
                if (!string.IsNullOrWhiteSpace(pemB64))
                {
                    try
                    {
                        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(pemB64));
                        if (!string.IsNullOrWhiteSpace(decoded))
                            pem = decoded;
                    }
                    catch
                    {
                        // ignore decode errors and fall through to other options
                    }
                }
            }

            // If pem still doesn't look valid, check if the env value is itself base64
            if (!string.IsNullOrWhiteSpace(pem) && (!pem.Contains("-----BEGIN") || !pem.Contains("-----END")))
            {
                try
                {
                    var maybe = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(pem));
                    if (maybe.Contains("-----BEGIN") && maybe.Contains("-----END"))
                        pem = maybe;
                }
                catch
                {
                    // not base64, continue
                }
            }

            if (!string.IsNullOrWhiteSpace(pem) && pem.Contains("-----BEGIN") && pem.Contains("-----END"))
                return pem;

            // Fall back to path
            var path = config["PRIVATE_KEY_PATH"] ?? Environment.GetEnvironmentVariable("PRIVATE_KEY_PATH");
            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                return System.IO.File.ReadAllText(path);

            throw new InvalidOperationException("PRIVATE_KEY_PEM (raw or base64) or PRIVATE_KEY_PATH must be set and contain a valid PEM-encoded private key");
        }

        private static string DecryptFlowRequest(FlowEncryptedRequest req, RSA rsa, out byte[] aesKey, out byte[] requestIv)
        {
            requestIv = Convert.FromBase64String(req.initial_vector);

            var encAesKey = Convert.FromBase64String(req.encrypted_aes_key);
            aesKey = rsa.Decrypt(encAesKey, RSAEncryptionPadding.OaepSHA256);

            var encData = Convert.FromBase64String(req.encrypted_flow_data);

            var cipher = new GcmBlockCipher(new AesEngine());
            var param = new AeadParameters(new KeyParameter(aesKey), 128, requestIv);
            cipher.Init(false, param);

            byte[] plain = new byte[cipher.GetOutputSize(encData.Length)];
            int len = cipher.ProcessBytes(encData, 0, encData.Length, plain, 0);
            len += cipher.DoFinal(plain, len);

            return Encoding.UTF8.GetString(plain, 0, len);
        }

        private static string EncryptFlowResponse(object responseObj, byte[] aesKey, byte[] requestIv)
        {
            var json = JsonSerializer.Serialize(responseObj);
            var plain = Encoding.UTF8.GetBytes(json);

            var iv = FlipIv(requestIv);

            var cipher = new GcmBlockCipher(new AesEngine());
            var param = new AeadParameters(new KeyParameter(aesKey), 128, iv);
            cipher.Init(true, param);

            byte[] cipherText = new byte[cipher.GetOutputSize(plain.Length)];
            int len = cipher.ProcessBytes(plain, 0, plain.Length, cipherText, 0);
            cipher.DoFinal(cipherText, len);

            return Convert.ToBase64String(cipherText);
        }
    }
}
