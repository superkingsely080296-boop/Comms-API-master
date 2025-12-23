using FusionComms.Data;
using FusionComms.DTOs.WhatsApp;
using FusionComms.Services.WhatsApp;
using FusionComms.Services.WhatsApp.Restaurants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace FusionComms.Controllers.WhatsApp
{
    [ApiController]
    [Route("api/{businessId}/[controller]")]
    public class WhatsAppController : ControllerBase
    {
        private readonly WhatsAppMessagingService _messagingService;
        private readonly WhatsAppCatalogService _catalogService;
        private readonly WhatsAppOnboardingService _onboardingService;
        private readonly AppDbContext _db;

        public WhatsAppController(WhatsAppMessagingService messagingService, WhatsAppCatalogService catalogService, WhatsAppOnboardingService onboardingService, AppDbContext db)
        {
            _messagingService = messagingService;
            _catalogService = catalogService;
            _onboardingService = onboardingService;
            _db = db;
        }

        [HttpPost("send")]
        public async Task<IActionResult> SendMessage(
            [FromRoute] string businessId,
            [FromBody] WhatsAppMessageDto messageDto)
        {
            var response = await _messagingService.SendTemplateMessageAsync(businessId, messageDto);
            return response.Success ? Ok(response) : BadRequest(response);
        }

        [HttpPost("send-text")]
        public async Task<IActionResult> SendTextMessage(
            [FromRoute] string businessId,
            [FromBody] WhatsAppTextMessageDto textMessageDto)
        {
            var response = await _messagingService.SendTextMessageAsync(businessId, textMessageDto.PhoneNumber, textMessageDto.TextContent);
            return response.Success ? Ok(response) : BadRequest(response);
        }

        [HttpGet("messages")]
        public async Task<IActionResult> GetMessages(
            [FromRoute] string businessId,
            [FromQuery] DateTime? startDate,
            [FromQuery] DateTime? endDate,
            [FromQuery] string status,
            [FromQuery] string receiverPhone)
        {
            receiverPhone = receiverPhone?.Replace(" ", "+");
            var response = await _messagingService.GetMessagesAsync(businessId, startDate, endDate, status, receiverPhone);
            return response.Success ? Ok(response) : BadRequest(response);
        }

        [HttpGet("statistics")]
        public async Task<IActionResult> GetMessageStatistics(
            [FromRoute] string businessId,
            [FromQuery][Required] DateTime startDate,
            [FromQuery][Required] DateTime endDate)
        {
            var response = await _messagingService.GetMessageStatisticsAsync(businessId, startDate, endDate);
            return response.Success ? Ok(response) : BadRequest(response);
        }

        [HttpPost("upload-media")]
        public async Task<IActionResult> UploadMedia(
            [FromRoute] string businessId,
            [FromForm] IFormFile file,
            [FromForm] string caption = null)
        {
            var response = await _messagingService.UploadMediaAsync(businessId, file, caption);
            return response.Success ? Ok(response) : BadRequest(response);
        }

        [HttpGet("templates")]
        public async Task<IActionResult> GetApprovedTemplates(
            [FromRoute] string businessId,
            [FromQuery] string templateId = null,
            [FromQuery] string templateName = null)
        {
            var response = await _messagingService.GetApprovedTemplatesAsync(businessId, templateId, templateName);
            return response.Success ? Ok(response) : BadRequest(response);
        }

        [HttpGet("media")]
        public async Task<IActionResult> GetUploadedMedia(
            [FromRoute] string businessId)
        {
            var response = await _messagingService.GetUploadedMediaAsync(businessId);
            return response.Success ? Ok(response) : BadRequest(response);
        }

        [HttpPost("catalog-products")]
        public async Task<IActionResult> AddProducts(
            [FromRoute] string businessId,
            [FromBody] List<WhatsAppProductDto> products)
        {
            var result = await _catalogService.AddProductsAsync(businessId, products);
            return result.Success ? Ok(result) : BadRequest(result);
        }

        [HttpPut("catalog-products/{retailerId}")]
        public async Task<IActionResult> UpdateProduct(
            [FromRoute] string businessId,
            [FromRoute] string retailerId,
            [FromBody] WhatsAppProductUpdateDto update)
        {
            var result = await _catalogService.UpdateProductAsync(businessId, retailerId, update);
            return result.Success ? Ok(result) : BadRequest(result);
        }

        [HttpDelete("catalog-products")]
        public async Task<IActionResult> DeleteProducts(
            [FromRoute] string businessId,
            [FromBody] List<string> retailerIds)
        {
            var result = await _catalogService.DeleteProductsAsync(businessId, retailerIds);
            return result.Success ? Ok(result) : BadRequest(result);
        }

        [HttpPut("featured-products")]
        public async Task<IActionResult> UpdateFeaturedProducts(
            [FromRoute] string businessId,
            [FromBody] FeaturedProductsRequestDto request)
        {
            var result = await _catalogService.UpdateFeaturedProductsAsync(businessId, request.RetailerIds);
            return result.Success ? Ok(result) : BadRequest(result);
        }

        [HttpPost("onboard-restaurant")]
        public async Task<IActionResult> OnboardRestaurant(
            [FromBody] WhatsAppOnboardingRequest request)
        {
            var result = await _onboardingService.OnboardRestaurantAsync(request);
            return result.Success ? Ok(result) : BadRequest(result);
        }

        // [HttpPost("onboarding/exchange-code")]
        // public async Task<IActionResult> TestExchangeCode(
        //     [FromBody] TestExchangeCodeRequest request)
        // {
        //     var result = await _onboardingService.ExchangeCodeForTokenAsync(request.Code, request.AppId);
        //     return Ok(new { success = !string.IsNullOrEmpty(result), token = result });
        // }

        // [HttpPost("onboarding/subscribe-webhooks")]
        // public async Task<IActionResult> TestSubscribeWebhooks(
        //     [FromBody] TestWebhookRequest request)
        // {
        //     var result = await _onboardingService.SubscribeToWebhooksAsync(request.WabaId, request.BusinessToken);
        //     return Ok(new { success = result });
        // }

        // [HttpPost("onboarding/register-phone")]
        // public async Task<IActionResult> TestRegisterPhone(
        //     [FromBody] TestPhoneRequest request)
        // {
        //     var result = await _onboardingService.RegisterPhoneNumberAsync(request.PhoneNumberId, request.BusinessToken);
        //     return Ok(new { success = result });
        // }

        // [HttpPost("onboarding/setup-catalog")]
        // public async Task<IActionResult> TestSetupCatalog(
        //     [FromBody] TestCatalogRequest request)
        // {
        //     var result = await _onboardingService.SetupCatalogAsync(request.BusinessId, request.WabaId, request.BusinessToken, request.BusinessName, request.PhoneNumberId);
        //     return Ok(new { success = !string.IsNullOrEmpty(result), catalogId = result });
        // }

        // [HttpPost("onboarding/create-template")]
        // public async Task<IActionResult> TestCreateTemplate(
        //     [FromBody] TestTemplateRequest request)
        // {
        //     var result = await _onboardingService.CreateMessageTemplateAsync(request.WabaId, request.BusinessToken, request.BusinessName);
        //     return Ok(new { success = !string.IsNullOrEmpty(result), templateId = result });
        // }

        [HttpGet("business-by-restaurant/{restaurantId}")]
        public async Task<IActionResult> GetBusinessByRestaurant(
            [FromRoute] string restaurantId)
        {
            var business = await _messagingService.GetBusinessByRestaurantIdAsync(restaurantId);
            return business == null
                ? NotFound(new { success = false, message = $"No business found for restaurant ID: {restaurantId}" })
                : Ok(new { success = true, businessId = business.BusinessId });
        }

        [HttpGet("flow")]
        public async Task<IActionResult> GetDeliveryFlow(
            [FromRoute] string businessId)
        {
            var business = await _db.WhatsAppBusinesses.FindAsync(businessId);
            if (business == null)
            {
                return NotFound(new { success = false, message = "Business not found" });
            }

            return Ok(new { success = true, flowJson = business.DeliveryFlowJson });
        }

        [HttpPost("flow")]
        public async Task<IActionResult> SaveDeliveryFlow(
            [FromRoute] string businessId,
            [FromBody] SaveFlowRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.FlowJson))
            {
                return BadRequest(new { success = false, message = "FlowJson is required" });
            }

            var business = await _db.WhatsAppBusinesses.FindAsync(businessId);
            if (business == null)
            {
                return NotFound(new { success = false, message = "Business not found" });
            }

            business.DeliveryFlowJson = request.FlowJson;
            _db.WhatsAppBusinesses.Update(business);
            await _db.SaveChangesAsync();

            return Ok(new { success = true, message = "Flow saved successfully" });
        }
    }

    public class SaveFlowRequest
    {
        public string FlowJson { get; set; }
    }
}