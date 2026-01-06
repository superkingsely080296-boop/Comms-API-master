using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
            _logger.LogInformation("🚀 FLOW HIT");

            try
            {
                var privatePem = GetPrivateKeyPem(_configuration);

                using var rsa = RSA.Create();
                rsa.ImportFromPem(privatePem);

                var decryptedJson = DecryptFlowRequest(req, rsa, out var aesKey, out var iv);
                _logger.LogInformation("🔓 Decrypted Payload");

                var client = _httpClientFactory.CreateClient();
                var apiResponse = await client.GetAsync("https://cjendpoint.onrender.com/api/areas");

                if (!apiResponse.IsSuccessStatusCode)
                    throw new Exception("Failed to fetch delivery areas");

                var rawAreas = await apiResponse.Content.ReadFromJsonAsync<List<ExternalArea>>();

                var deliveryAreas = new List<object>();
                if (rawAreas != null)
                {
                    foreach (var a in rawAreas)
                    {
                        deliveryAreas.Add(new { id = a.id, title = a.title });
                    }
                }

                var response = new
                {
                    version = "3.0",
                    screen = "screen_asnlyt",
                    data = new
                    {
                        delivery_areas = deliveryAreas,
                        status = "active"
                    }
                };

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
            var pem = config["PRIVATE_KEY_PEM"] ?? Environment.GetEnvironmentVariable("PRIVATE_KEY_PEM");
            if (!string.IsNullOrWhiteSpace(pem))
                return pem;

            var path = config["PRIVATE_KEY_PATH"] ?? Environment.GetEnvironmentVariable("PRIVATE_KEY_PATH");
            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                return System.IO.File.ReadAllText(path);

            throw new InvalidOperationException("PRIVATE_KEY_PEM or PRIVATE_KEY_PATH must be set");
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
