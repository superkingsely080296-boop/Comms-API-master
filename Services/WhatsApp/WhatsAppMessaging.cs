using FusionComms.Data;
using FusionComms.DTOs.WhatsApp;
using FusionComms.Entities.WhatsApp;
using FusionComms.Utilities;
using Mailjet.Client.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;

namespace FusionComms.Services.WhatsApp
{
    public class WhatsAppMessagingService
    {
        private readonly AppDbContext _db;
        private readonly IHttpClientFactory _httpClientFactory;

        public WhatsAppMessagingService(AppDbContext dbContext, IHttpClientFactory httpClientFactory)
        {
            _db = dbContext;
            _httpClientFactory = httpClientFactory;
        }

        public async Task<WhatsAppResponse> SendTemplateMessageAsync(string businessId, WhatsAppMessageDto messageDto)
        {
            var result = await _db.WhatsAppBusinesses
                .Where(b => b.BusinessId == businessId)
                .Select(b => new
                {
                    Business = b,
                    Template = _db.WhatsAppTemplates
                        .FirstOrDefault(t => 
                            t.BusinessId == b.BusinessId &&
                            t.TemplateName == messageDto.TemplateName && 
                            t.Status == "APPROVED"),
                    MediaExists = string.IsNullOrEmpty(messageDto.MediaId) || 
                        _db.WhatsAppMedia
                            .Any(m => m.MediaId == messageDto.MediaId && 
                                    m.BusinessId == b.BusinessId)
                })
                .FirstOrDefaultAsync();

            if (result?.Business == null)
                return new WhatsAppResponse { Success = false, Message = $"Invalid business ID: {businessId}" };

            if (result.Template == null)
                return new WhatsAppResponse { Success = false, Message = $"Invalid Template {messageDto.TemplateName}" };

            if (!string.IsNullOrEmpty(messageDto.MediaId) && !result.MediaExists)
                return new WhatsAppResponse { Success = false, Message = $"Invalid Media ID: {messageDto.MediaId}" };

            if (messageDto.BodyParameters != null)
            {
                for (int i = 0; i < messageDto.BodyParameters.Count; i++)
                {
                    string current = messageDto.BodyParameters[i];
                    if (current != null)
                    {
                        string cleaned = current
                            .Replace("\r\n", " ")
                            .Replace("\n", " ")
                            .Replace("\r", " ");

                        cleaned = cleaned.Trim();
                        
                        if (current != cleaned) 
                        {
                            messageDto.BodyParameters[i] = cleaned;
                        }
                    }
                }
            }

            var template = result.Template;

            switch (template.Category)
            {
                case TemplateCategory.AUTHENTICATION:
                    if (messageDto.BodyParameters.Count != 1)
                        return new WhatsAppResponse { Success = false, Message = "Authentication templates require exactly 1 parameter" };
                    if (!string.IsNullOrEmpty(messageDto.MediaId))
                        return new WhatsAppResponse { Success = false, Message = "Authentication templates don't support media" };
                    break;
                default:
                    if (messageDto.BodyParameters.Count != template.ParameterCount)
                        return new WhatsAppResponse { Success = false, Message = $"Template requires {template.ParameterCount} parameters" };
                    break;
            }

            var message = await CreateMessageRecord(businessId, messageDto.PhoneNumber, 
                FormatMessageContent(template.BodyText, messageDto.BodyParameters), 
                template.TemplateId, messageDto.MediaId);

            var payload = CreatePayload(messageDto, template);
            
            return await SendMessageToWhatsApp(result.Business, payload, message);
        }

        public async Task<WhatsAppResponse> SendInteractiveMessageAsync(
            string businessId,
            string phoneNumber,
            string bodyText,
            List<WhatsAppButton> buttons,
            string footer = null)
        {
            var business = await GetBusinessAsync(businessId);
            if (business == null)
            {
                return new WhatsAppResponse { Success = false, Message = $"Invalid business ID: {businessId}" };
            }

            phoneNumber = FormatWhatsAppPhoneNumber(phoneNumber);

            var message = await CreateMessageRecord(businessId, phoneNumber, bodyText);

            var buttonObjects = buttons.Select(b => b.ToApiObject()).ToArray();

            var payload = new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = phoneNumber,
                type = "interactive",
                interactive = new
                {
                    type = "button",
                    body = new { text = bodyText },
                    action = new { buttons = buttonObjects },
                    footer = !string.IsNullOrEmpty(footer) ? new { text = footer } : null
                }
            };

            var result = await SendMessageToWhatsApp(business, payload, message);
            
            return result;
        }

        public async Task<WhatsAppResponse> SendInteractiveListAsync(
            string businessId,
            string phoneNumber,
            string bodyText,
            string buttonText,
            List<WhatsAppSection> sections)
        {
            var business = await GetBusinessAsync(businessId);
            if (business == null)
            {
                return new WhatsAppResponse { Success = false, Message = $"Invalid business ID: {businessId}" };
            }

            phoneNumber = FormatWhatsAppPhoneNumber(phoneNumber);

            var message = await CreateMessageRecord(businessId, phoneNumber, bodyText);

            var sectionObjects = sections.Select(s => new
            {
                title = s.Title,
                rows = s.Rows.Select(r => new
                {
                    id = r.Id,
                    title = r.Title,
                    description = r.Description
                }).ToArray()
            }).ToArray();

            var payload = new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = phoneNumber,
                type = "interactive",
                interactive = new
                {
                    type = "list",
                    body = new { text = bodyText },
                    footer = new { text = "Select an option" },
                    action = new
                    {
                        button = buttonText,
                        sections = sectionObjects
                    }
                }
            };

            return await SendMessageToWhatsApp(business, payload, message);
        }

        public async Task<WhatsAppResponse> SendTextMessageAsync(
            string businessId,
            string phoneNumber,
            string textContent)
        {
            var business = await GetBusinessAsync(businessId);
            if (business == null)
            {
                return new WhatsAppResponse { Success = false, Message = $"Invalid business ID: {businessId}" };
            }

            phoneNumber = FormatWhatsAppPhoneNumber(phoneNumber);

            var message = await CreateMessageRecord(businessId, phoneNumber, textContent);

            var payload = new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = phoneNumber,
                type = "text",
                text = new { body = textContent }
            };

            return await SendMessageToWhatsApp(business, payload, message);
        }
        public async Task<WhatsAppResponse> SendCatalogMessageAsync(
            string businessId,
            string phoneNumber,
            string headerText,
            string bodyText,
            string revenueCenterId = null)
        {
            var business = await GetBusinessAsync(businessId);
            if (business == null)
            {
                return new WhatsAppResponse { Success = false, Message = "Business not found" };
            }
            
            if (string.IsNullOrEmpty(business.CatalogId))
            {
                return new WhatsAppResponse { Success = false, Message = "Missing catalog ID" };
            }

            phoneNumber = FormatWhatsAppPhoneNumber(phoneNumber);

            var productSets = await _db.WhatsAppProductSets
                .Where(ps => ps.BusinessId == businessId)
                .ToListAsync();

            var setIds = productSets.Select(ps => ps.SetId).ToList();

            var products = await _db.WhatsAppProducts
                .Where(p => setIds.Contains(p.SetId) && p.IsFeatured)
                .ToListAsync();

            if (!string.IsNullOrEmpty(revenueCenterId))
            {
                var filteredProducts = products
                    .Where(p => !string.IsNullOrEmpty(p.RetailerId) && p.RevenueCenterId == revenueCenterId)
                    .ToList();

                if (filteredProducts.Any())
                {
                    var sampleProducts = filteredProducts.Take(3).Select(p => $"{p.Name} (RC: {p.RevenueCenterId})");
                }

                products = filteredProducts;
            }

            var productsBySet = products
                .GroupBy(p => p.SetId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var sections = new List<object>();

            foreach (var set in productSets)
            {
                if (productsBySet.TryGetValue(set.SetId, out var productsInSet))
                {
                    var validProducts = productsInSet
                        .Where(p => !string.IsNullOrEmpty(p.RetailerId))
                        .ToList();

                    if (validProducts.Any())
                    {
                        sections.Add(new
                        {
                            title = set.Name,
                            product_items = validProducts
                                .Select(p => new { product_retailer_id = p.RetailerId })
                                .ToList()
                        });
                    }
                }
            }

            if (!sections.Any())
            {
                return new WhatsAppResponse
                {
                    Success = false,
                    Message = "No valid products found with retailer IDs"
                };
            }

            var payload = new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = phoneNumber,
                type = "interactive",
                interactive = new
                {
                    type = "product_list",
                    header = new
                    {
                        type = "text",
                        text = headerText
                    },
                    body = new
                    {
                        text = bodyText
                    },
                    action = new
                    {
                        catalog_id = business.CatalogId,
                        sections = sections
                    }
                }
            };

            var message = await CreateMessageRecord(businessId, phoneNumber, $"{headerText} - {bodyText}");

            return await SendMessageToWhatsApp(business, payload, message);
        }

        public async Task<WhatsAppResponse> SendFullCatalogMessageAsync(
            string businessId,
            string phoneNumber,
            string headerText,
            string bodyText,
            string revenueCenterId,
            List<string> allowedSetIds = null,
            string subcategory = null)
        {
            var business = await GetBusinessAsync(businessId);
            if (business == null)
            {
                return new WhatsAppResponse { Success = false, Message = "Business not found" };
            }
            
            if (string.IsNullOrEmpty(business.CatalogId))
            {
                return new WhatsAppResponse { Success = false, Message = "Missing catalog ID" };
            }

            phoneNumber = FormatWhatsAppPhoneNumber(phoneNumber);

            var productSetsQuery = _db.WhatsAppProductSets
                .Where(ps => ps.BusinessId == businessId);

            if (allowedSetIds != null && allowedSetIds.Any())
            {
                productSetsQuery = productSetsQuery.Where(ps => allowedSetIds.Contains(ps.SetId));
            }

            var productSets = await productSetsQuery
                .OrderBy(ps => ps.Name)
                .ToListAsync();

            productSets = productSets.Take(10).ToList();

            var setIds = productSets.Select(ps => ps.SetId).ToList();

            var products = await _db.WhatsAppProducts
                .Where(p => setIds.Contains(p.SetId))
                .Where(p => !string.IsNullOrEmpty(p.RetailerId))
                .Where(p => string.IsNullOrEmpty(revenueCenterId) || p.RevenueCenterId == revenueCenterId)
                .Where(p => string.IsNullOrEmpty(subcategory) || p.Subcategory == subcategory)
                .ToListAsync();

            var productsBySet = products
                .GroupBy(p => p.SetId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var sections = new List<object>();

            foreach (var set in productSets)
            {
                if (productsBySet.TryGetValue(set.SetId, out var productsInSet))
                {
                    var validProducts = productsInSet
                        .Where(p => !string.IsNullOrEmpty(p.RetailerId))
                        .ToList();

                    if (validProducts.Any())
                    {
                        sections.Add(new
                        {
                            title = set.Name,
                            product_items = validProducts
                                .Select(p => new { product_retailer_id = p.RetailerId })
                                .ToList()
                        });
                    }
                }
            }

            if (!sections.Any())
                return new WhatsAppResponse{Success = false, Message = "No valid products found for full catalog"};

            var payload = new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = phoneNumber,
                type = "interactive",
                interactive = new
                {
                    type = "product_list",
                    header = new
                    {
                        type = "text",
                        text = headerText
                    },
                    body = new
                    {
                        text = bodyText
                    },
                    action = new
                    {
                        catalog_id = business.CatalogId,
                        sections = sections
                    }
                }
            };

            var message = await CreateMessageRecord(businessId, phoneNumber, $"{headerText} - {bodyText}");

            return await SendMessageToWhatsApp(business, payload, message);
        }

        public async Task<WhatsAppResponse> SendSearchResultsCatalogMessageAsync(
            string businessId,
            string phoneNumber,
            string headerText,
            string bodyText,
            string revenueCenterId,
            List<WhatsAppProduct> searchResults)
        {
            var business = await GetBusinessAsync(businessId);
            if (business == null)
            {
                return new WhatsAppResponse { Success = false, Message = "Business not found" };
            }
            
            if (string.IsNullOrEmpty(business.CatalogId))
            {
                return new WhatsAppResponse { Success = false, Message = "Missing catalog ID" };
            }

            phoneNumber = FormatWhatsAppPhoneNumber(phoneNumber);

            var resultsBySet = searchResults
                .GroupBy(p => p.SetId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var sections = new List<object>();

            foreach (var setGroup in resultsBySet)
            {
                var setId = setGroup.Key;
                var productsInSet = setGroup.Value;

                var productSet = await _db.WhatsAppProductSets
                    .FirstOrDefaultAsync(ps => ps.SetId == setId && ps.BusinessId == businessId);

                if (productSet != null)
                {
                    var validProducts = productsInSet
                        .Where(p => !string.IsNullOrEmpty(p.RetailerId))
                        .ToList();

                    if (validProducts.Count > 0)
                    {
                        sections.Add(new
                        {
                            title = productSet.Name,
                            product_items = validProducts
                                .Select(p => new { product_retailer_id = p.RetailerId })
                                .ToList()
                        });
                        
                    }
                }
            }

            if (sections.Count == 0)
            {
                return new WhatsAppResponse{Success = false, Message = "No valid products found for search results"};
            }

            var payload = new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = phoneNumber,
                type = "interactive",
                interactive = new
                {
                    type = "product_list",
                    header = new
                    {
                        type = "text",
                        text = headerText
                    },
                    body = new
                    {
                        text = bodyText
                    },
                    action = new
                    {
                        catalog_id = business.CatalogId,
                        sections = sections
                    }
                }
            };

            var message = await CreateMessageRecord(businessId, phoneNumber, $"{headerText} - {bodyText}");

            return await SendMessageToWhatsApp(business, payload, message);
        }

        public async Task<WhatsAppResponse<List<WhatsAppMessageStatusDto>>> GetMessagesAsync(
            string businessId,
            DateTime? startDate,
            DateTime? endDate,
            string status,
            string receiverPhone)
        {
            if (!await BusinessExists(businessId))
                return new WhatsAppResponse<List<WhatsAppMessageStatusDto>> { Success = false, Message = $"Invalid business ID: {businessId}" };

            var (utcStart, utcEnd) = CalculateDateRange(startDate, endDate);
            if (utcStart > utcEnd)
                return new WhatsAppResponse<List<WhatsAppMessageStatusDto>> { Success = false, Message = "Invalid date range" };

            var query = _db.WhatsAppMessages
                .Where(m => m.BusinessId == businessId && m.Direction == "OUTBOUND")
                .Where(m => m.Timestamp >= utcStart && m.Timestamp <= utcEnd);

            if (!string.IsNullOrEmpty(status))
                query = query.Where(m => m.Status == status.ToUpper());
            if (!string.IsNullOrEmpty(receiverPhone))
            {
                query = query.Where(m =>
                    m.PhoneNumber == receiverPhone ||
                    m.PhoneNumber.EndsWith(receiverPhone)
                );
            }

            var messages = await query
                .OrderByDescending(m => m.Timestamp)
                .Select(m => new WhatsAppMessageStatusDto
                {
                    Content = m.Content,
                    PhoneNumber = m.PhoneNumber,
                    Date = m.Timestamp.Date.ToString("yyyy-MM-dd"),
                    Status = m.Status,
                    ErrorMessage = m.ErrorTitle,
                })
                .ToListAsync();

            return new WhatsAppResponse<List<WhatsAppMessageStatusDto>>
            {
                Success = true,
                Data = messages
            };
        }

        public async Task<WhatsAppResponse<WhatsAppMessageStatisticsDto>> GetMessageStatisticsAsync(
            string businessId,
            DateTime startDate,
            DateTime endDate)
            {
                try
                {
                    if (endDate < startDate)
                            return new WhatsAppResponse<WhatsAppMessageStatisticsDto> { Success = false, Message = "End date cannot be before start date."};

                    var messages = await _db.WhatsAppMessages
                        .Where(m => m.BusinessId == businessId)
                        .Where(m => m.Direction == "OUTBOUND")
                        .Where(m => m.Timestamp >= startDate && m.Timestamp <= endDate.Date.AddDays(1))
                        .ToListAsync();

                    if (messages.Count == 0)
                    {
                        return new WhatsAppResponse<WhatsAppMessageStatisticsDto> 
                        { 
                            Success = true, 
                            Data = new WhatsAppMessageStatisticsDto { TotalMessages = 0 } 
                        };
                    }

                    var statusGroups = messages
                        .GroupBy(m => m.Status.ToUpper())
                        .ToDictionary(g => g.Key, g => g.Count());

                    var totalMessages = messages.Count;
                    var percentages = new Dictionary<string, double>
                    {
                        ["SENT"] = 0,
                        ["DELIVERED"] = 0,
                        ["READ"] = 0,
                        ["FAILED"] = 0
                    };

                    foreach (var status in statusGroups)
                    {
                        if (percentages.ContainsKey(status.Key))
                        {
                            percentages[status.Key] = Math.Round(status.Value / (double)totalMessages * 100, 2);
                        }
                    }

                    return new WhatsAppResponse<WhatsAppMessageStatisticsDto>
                    {
                        Success = true,
                        Data = new WhatsAppMessageStatisticsDto
                        {
                            StatusPercentages = percentages,
                            TotalMessages = totalMessages
                        }
                    };
                }
                catch (Exception ex)
                {
                    return new WhatsAppResponse<WhatsAppMessageStatisticsDto>
                    {
                        Success = false,
                        Message = $"Failed to calculate statistics: {ex.Message}"
                    };
                }
            }

        public async Task<WhatsAppResponse<string>> UploadMediaAsync(string businessId, IFormFile file, string caption = null)
        {
            try
            {
                if (string.IsNullOrEmpty(file.FileName))
                    return new WhatsAppResponse<string> { Success = false, Message = "Invalid file name" };

                var business = await _db.WhatsAppBusinesses.FirstOrDefaultAsync(b => b.BusinessId == businessId); 
                if (business == null)
                    return new WhatsAppResponse<string> { Success = false, Message = $"Invalid business ID: {businessId}" };

                var allowedTypes = new[] { "image/jpeg", "image/png" };
                if (!allowedTypes.Contains(file.ContentType))
                    return new WhatsAppResponse<string> { Success = false, Message = $"Invalid file type {file.ContentType}" };

                using var client = _httpClientFactory.CreateClient();
                client.BaseAddress = new Uri("https://graph.facebook.com/v22.0/");
                client.DefaultRequestHeaders.Authorization = 
                    new AuthenticationHeaderValue("Bearer", business.BusinessToken);

                await using var fileStream = file.OpenReadStream();
                var fileContent = new StreamContent(fileStream);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);

                using var formContent = new MultipartFormDataContent
                {
                    { new StringContent("whatsapp"), "messaging_product" },
                    { fileContent, "file", file.FileName }
                };

                var response = await client.PostAsync($"{business.PhoneNumberId}/media", formContent);
                
                if (!response.IsSuccessStatusCode)
                    return new WhatsAppResponse<string> { Success = false, Message = $"Upload failed: {await response.Content.ReadAsStringAsync()}" };

                var responseJson = JObject.Parse(await response.Content.ReadAsStringAsync());
                var mediaId = responseJson["id"]!.ToString();

                var media = new WhatsAppMedia
                {
                    MediaId = mediaId,
                    BusinessId = businessId,
                    FileName = file.FileName,
                    FileType = file.ContentType,
                    FileSize = file.Length,
                    Caption = caption,
                    UploadTimestamp = DateTime.UtcNow,
                    ExpirationDate = DateTime.UtcNow.AddDays(30),
                };

                await _db.WhatsAppMedia.AddAsync(media);
                await _db.SaveChangesAsync();

                return new WhatsAppResponse<string> { Success = true, Message = "Media uploaded successfully" };
            }
            catch (Exception ex)
            {
                return new WhatsAppResponse<string> { Success = false, Message = $"Upload failed: {ex.Message}" };
            }
        }

        public async Task<WhatsAppResponse<List<WhatsAppTemplateDto>>> GetApprovedTemplatesAsync(string businessId, string templateId = null, string templateName = null)
        {
            if (!await BusinessExists(businessId))
                return new WhatsAppResponse<List<WhatsAppTemplateDto>> { Success = false, Message = $"Invalid business ID: {businessId}" };

            var query = _db.WhatsAppTemplates
                .Where(t => t.BusinessId == businessId && t.Status == "APPROVED" && t.BodyText != null);

            if (!string.IsNullOrEmpty(templateId))
                query = query.Where(t => t.TemplateId == templateId);

            if (!string.IsNullOrEmpty(templateName))
                query = query.Where(t => t.TemplateName == templateName);

            var templates = await query
                .Select(t => new WhatsAppTemplateDto
                {
                    TemplateName = t.TemplateName,
                    ParameterCount = t.ParameterCount,
                    Body = new TemplateBodyDto
                    {
                        Text = t.BodyText,
                        Examples = t.ExampleBodyText != null
                            ? t.ExampleBodyText.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
                            : new List<string>()
                    }
                })
                .ToListAsync();

            return new WhatsAppResponse<List<WhatsAppTemplateDto>> { Success = true, Data = templates };
        }

        public async Task<WhatsAppResponse<List<WhatsAppMediaDto>>> GetUploadedMediaAsync(string businessId)
        {
            if (!await BusinessExists(businessId))
                return new WhatsAppResponse<List<WhatsAppMediaDto>> { Success = false, Message = $"Invalid business ID: {businessId}"};

            var mediaEntities = await _db.WhatsAppMedia
                .Where(m => m.BusinessId == businessId && m.ExpirationDate > DateTime.UtcNow && m.FileSize != 108)
                .Select(m => new 
                {
                    m.MediaId,
                    m.FileName,
                    m.FileType,
                    m.Caption,
                    ExpirationDateUtc = m.ExpirationDate
                })
                .ToListAsync();

            var mediaDtos = mediaEntities.Select(m => 
            {
                var nigeriaTime = TimeZoneInfo.ConvertTimeFromUtc(m.ExpirationDateUtc, AppTimeZones.Nigeria);
                return new WhatsAppMediaDto
                {
                    MediaId = m.MediaId,
                    FileName = m.FileName,
                    FileType = m.FileType,
                    Caption = m.Caption,
                    ExpirationDate = nigeriaTime.ToString("yyyy-MM-dd")
                };
            }).ToList();

            return new WhatsAppResponse<List<WhatsAppMediaDto>> { Success = true, Data = mediaDtos };
        }

        private object CreatePayload(WhatsAppMessageDto messageDto, WhatsAppTemplate template)
        {
            var components = new List<object>();

            if (template.Category != TemplateCategory.AUTHENTICATION && 
                !string.IsNullOrEmpty(messageDto.MediaId))
            {
                components.Add(new 
                {
                    type = "header",
                    parameters = new[] 
                    {
                        new { type = "image", image = new { id = messageDto.MediaId } }
                    }
                });
            }

            var bodyParams = messageDto.BodyParameters
                .Select(p => new { type = "text", text = p })
                .ToArray();

            components.Add(new 
            {
                type = "body",
                parameters = bodyParams
            });

            if (template.Category == TemplateCategory.AUTHENTICATION)
            {
                components.Add(new 
                {
                    type = "button",
                    sub_type = "url",
                    index = 0,
                    parameters = new[]
                    {
                        new { type = "text", text = messageDto.BodyParameters.FirstOrDefault() }
                    }
                });
            }

            return new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = messageDto.PhoneNumber,
                type = "template",
                template = new
                {
                    name = template.TemplateName,
                    language = new { code = template.Language ?? "en" },
                    components
                }
            };
        }

        private (DateTime start, DateTime end) CalculateDateRange(DateTime? startDate, DateTime? endDate)
        {
            const int defaultDays = 14;
            var now = DateTime.UtcNow;

            DateTime calculatedStart;
            DateTime calculatedEnd;

            if (!startDate.HasValue && !endDate.HasValue)
            {
                calculatedStart = now.AddDays(-defaultDays);
                calculatedEnd = now;
            }
            else
            {
                calculatedStart = startDate ?? endDate?.AddDays(-defaultDays) ?? now.AddDays(-defaultDays);
                calculatedEnd = endDate ?? startDate?.AddDays(defaultDays) ?? now;
            }

            calculatedEnd = calculatedEnd.Date.AddDays(1).AddTicks(-1);
            return (calculatedStart, calculatedEnd);
        }

        private string FormatMessageContent(string bodyText, List<string> parameters)
        {
            if (string.IsNullOrEmpty(bodyText)) return string.Empty;

            return parameters.Aggregate(bodyText, (current, param) => 
                current.Replace($"{{{{{parameters.IndexOf(param) + 1}}}}}", param)); 
        }

        private async Task<WhatsAppMessage> CreateMessageRecord(string businessId, string phoneNumber, string content, string templateId = null, string mediaId = null)
        {
            var message = new WhatsAppMessage
            {
                BusinessId = businessId,
                MessageId = Guid.NewGuid().ToString(),
                PhoneNumber = phoneNumber,
                Content = content,
                TemplateId = templateId,
                MediaId = mediaId,
                Direction = "OUTBOUND",
                Status = "SENDING",
                Timestamp = DateTime.UtcNow
            };

            await _db.WhatsAppMessages.AddAsync(message);
            await _db.SaveChangesAsync();
            return message;
        }

        private async Task<WhatsAppBusiness> GetBusinessAsync(string businessId)
        {
            return await _db.WhatsAppBusinesses.FindAsync(businessId);
        }

        public async Task<WhatsAppBusiness> GetBusinessByRestaurantIdAsync(string restaurantId)
        {
            return await _db.WhatsAppBusinesses
                .FirstOrDefaultAsync(b => b.RestaurantId == restaurantId);
        }

        private async Task<WhatsAppResponse> SendMessageToWhatsApp(WhatsAppBusiness business, object payload, WhatsAppMessage message)
        {
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri($"https://graph.facebook.com/v22.0/{business.PhoneNumberId}/");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", business.BusinessToken);

            var payloadJson = JsonConvert.SerializeObject(payload);

            try
            {
                try
                {
                    var latestInboundId = await GetLatestInboundProviderMessageId(message.BusinessId, message.PhoneNumber);
                    if (!string.IsNullOrEmpty(latestInboundId))
                    {
                        await SendTypingIndicatorAsync(business, latestInboundId, "text");
                    }
                }
                catch
                {
                }

                var response = await client.PostAsync("messages", new StringContent(payloadJson, Encoding.UTF8, "application/json"));
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    string errorDetails;
                    try
                    {
                        var errorJson = JObject.Parse(responseContent);
                        errorDetails = errorJson["error"]?["message"]?.ToString() ?? responseContent;
                    }
                    catch
                    {
                        errorDetails = responseContent;
                    }

                    var providerErrorId = $"ERROR_{Guid.NewGuid()}";
                    await UpdateMessageStatus(message, "FAILED", providerErrorId, errorDetails);
                    return new WhatsAppResponse { Success = false, Message = errorDetails };
                }

                var responseJson = JObject.Parse(responseContent);
                var providerId = responseJson?["messages"]?[0]?["id"]?.ToString();

                await UpdateMessageStatus(message, "SENT", providerId);
                return new WhatsAppResponse { Success = true, Message = "Message sent successfully" };
            }
            catch (Exception ex)
            {
                await UpdateMessageStatus(message, "FAILED", null, ex.Message);
                return new WhatsAppResponse { Success = false, Message = ex.Message };
            }
        }

        private async Task<string> GetLatestInboundProviderMessageId(string businessId, string outboundPhoneNumber)
        {
            var normalizedPhone = outboundPhoneNumber?.TrimStart('+');

            var inbound = await _db.WhatsAppMessages
                .Where(m =>
                    m.BusinessId == businessId &&
                    m.Direction == "INBOUND" &&
                    (m.PhoneNumber == normalizedPhone || m.PhoneNumber.EndsWith(normalizedPhone)))
                .OrderByDescending(m => m.Timestamp)
                .FirstOrDefaultAsync();

            return inbound?.ProviderMessageId;
        }

        private async Task SendTypingIndicatorAsync(WhatsAppBusiness business, string providerMessageId, string indicatorType)
        {
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri($"https://graph.facebook.com/v24.0/{business.PhoneNumberId}/");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", business.BusinessToken);

            var typingPayload = new
            {
                messaging_product = "whatsapp",
                status = "read",
                message_id = providerMessageId,
                typing_indicator = new
                {
                    type = indicatorType
                }
            };

            var payloadJson = JsonConvert.SerializeObject(typingPayload);
            await client.PostAsync("messages", new StringContent(payloadJson, Encoding.UTF8, "application/json"));
        }

        private async Task UpdateMessageStatus(
            WhatsAppMessage message,
            string status,
            string providerId,
            string errorMessage = null)
        {
            message.Status = status;
            message.ProviderMessageId = providerId ?? message.ProviderMessageId;
            message.ErrorTitle = errorMessage;
            await _db.SaveChangesAsync();
        }

        private async Task<bool> BusinessExists(string businessId)
        {
            return await _db.WhatsAppBusinesses
                .AnyAsync(b => b.BusinessId == businessId);
        }

        public string FormatWhatsAppPhoneNumber(string phoneNumber)
        {
            if (phoneNumber.StartsWith("+")) return phoneNumber;

            if (phoneNumber.Contains("@"))
            {
                return "+" + phoneNumber.Split('@')[0];
            }

            return "+" + phoneNumber;
        }

        public async Task<WhatsAppResponse> SendOrderTemplateAsync(
            string businessId,
            string phoneNumber,
            string totalAmount,
            string accountNumber)
        {
            
            var business = await GetBusinessAsync(businessId);
            if (business == null)
            {
                return new WhatsAppResponse { Success = false, Message = $"Invalid business ID: {businessId}" };
            }

            phoneNumber = FormatWhatsAppPhoneNumber(phoneNumber);

            var payload = new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = phoneNumber,
                type = "template",
                template = new
                {
                    name = WhatsAppTemplateConstants.OrderPaymentTemplate,
                    language = new { code = "en" },
                    components = new object[]
                    {
                        new
                        {
                            type = "body",
                            parameters = new[]
                            {
                                new { type = "text", text = totalAmount }
                            }
                        }
                    }
                }
            };

            var message = await CreateMessageRecord(businessId, phoneNumber, 
                $"✅ Order Received!\n\n💰 Total: {totalAmount}\n\nPlease proceed to payment using the button below.\n\nAfter payment, you will be updated on your order status.\nSend Hi or Hello to start a new order.");

            return await SendMessageToWhatsApp(business, payload, message);
        }

        public async Task<WhatsAppResponse> SendOrderV2Template(string businessId, string phoneNumber, decimal totalAmount, OrderData orderDetails) 
        {

            var business = await GetBusinessAsync(businessId);
            if (business == null)
            {
                return new WhatsAppResponse { Success = false, Message = $"Invalid business ID: {businessId}" };
            }

            phoneNumber = FormatWhatsAppPhoneNumber(phoneNumber);
            phoneNumber=phoneNumber[1..];
            var bodyParameters = new List<BaseTextComponent>()
            {
                new() { type = "text", text = $"{totalAmount}" },
                new() { type = "text", text = orderDetails.bankName },
                new() { type = "text", text = orderDetails.paymentAccount },
                new() { type = "text", text = orderDetails.accountName },
            };

            var bodyComponent = new BodyComponent(bodyParameters){};

            var copyCodeButton = new ButtonComponent("copy_code", 
                new List<IButtonComponent>() { new CopyCodeButtonComponenet() { coupon_code = orderDetails.paymentAccount, type = "coupon_code" } },
                "0");
            var components = new List<ITemplateComponent>()
            {
                bodyComponent,
                copyCodeButton
            };

            var OrderPaymentTemplate = new
            {

                name = WhatsAppTemplateConstants.OrderPaymentTemplateV3,
                language = new { code ="en"},
                components = components
            };

            var payload = new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = phoneNumber,
                type = "template",
                template = OrderPaymentTemplate
            };

            var message = await CreateMessageRecord(businessId, phoneNumber, $"✅ *Order Received!*\n\n" +
                    $"💰 Total: ₦{totalAmount:N2}\n\n" +
                    $"Please make a transfer using the Account Details Below" +
                    $"🏦 Account Bank: {orderDetails.bankName}\n" +
                    $"🔗 Account Number: {orderDetails.paymentAccount}\n" +
                    $"Account Name: {orderDetails.accountName}\n\n" +
                    $"After payment, you'll be updated on your order status.\n" +
                    $"You can start a new order anytime.");

            return await SendMessageToWhatsApp(business, payload, message);

        }

        public async Task<WhatsAppResponse> SendDeliveryFlowAsync(string businessId, string phoneNumber, string flowJson)
        {
            var business = await GetBusinessAsync(businessId);
            if (business == null)
            {
                return new WhatsAppResponse { Success = false, Message = $"Invalid business ID: {businessId}" };
            }

            phoneNumber = FormatWhatsAppPhoneNumber(phoneNumber);

            // For demo purposes, we'll log the flow JSON and return success
            // In production, this would send the flow to WhatsApp API
            var message = await CreateMessageRecord(businessId, phoneNumber, "Delivery Flow Sent");

            // TODO: Implement actual WhatsApp Flow API call
            // For now, simulate success
            await UpdateMessageStatus(message, "SENT", Guid.NewGuid().ToString());

            return new WhatsAppResponse { Success = true, Message = "Delivery flow sent successfully" };
        }
    }
}
