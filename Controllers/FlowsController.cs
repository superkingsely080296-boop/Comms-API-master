using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
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
                if (string.Equals(action, "INIT", StringComparison.OrdinalIgnoreCase))
                {
                    var client = _httpClientFactory.CreateClient();

                    var apiUrl = "https://api.food-ease.io/api/v1/OrderCharge/whatsapp-list-charges?restaurantId=d91e02ba-50f3-4cd6-8607-8ffbbeeda2da&revCenterId=f97e82e5-c922-4f9c-bc9a-477641d72d11&sourceId=e059a93c-5423-4d07-a7df-8e48b38c428b&serviceType=Delivery&api-version=v1";
                    _logger.LogInformation("Calling external areas API: {Url}", apiUrl);

                    var apiResponse = await client.GetAsync(apiUrl);
                    if (!apiResponse.IsSuccessStatusCode)
                        throw new Exception("Failed to fetch delivery areas");

                    var rawApiResponse = await apiResponse.Content.ReadFromJsonAsync<ExternalApiResponse>();

                    var deliveryAreas = rawApiResponse?.data?.data?
                        .Where(x => x.chargeServices != null && x.chargeServices.Count > 0)
                        .Select(x => (object)new { id = x.chargeServices[0].orderCharge, title = x.chargeServices[0].orderCharge })
                        .ToList() ?? new List<object>();

                    response = new
                    {
                        version = "3.0",
                        screen = "screen_asnlyt",
                        data = new
                        {
                            delivery_areas = deliveryAreas,
                            status = "active"
                        }
                    };
                }
                else
                {
                    // fallback to previous behaviour: empty delivery areas
                    var deliveryAreas = new List<object>();
                    response = new
                    {
                        version = "3.0",
                        screen = "screen_asnlyt",
                        data = new
                        {
                            delivery_areas = deliveryAreas,
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
