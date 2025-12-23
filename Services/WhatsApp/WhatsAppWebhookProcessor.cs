using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FusionComms.Data;
using FusionComms.DTOs.WhatsApp;
using FusionComms.Entities.WhatsApp;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace FusionComms.Services.WhatsApp
{
    public abstract class WhatsAppWebhookProcessor : IWhatsAppWebhookProcessor
    {
        protected readonly AppDbContext _db;

        protected WhatsAppWebhookProcessor(AppDbContext dbContext)
        {
            _db = dbContext;
        }

        public async Task<IActionResult> ProcessWebhook(WhatsAppBusiness business, string payload)
        {
            if (business == null)
            {
                return new BadRequestResult();
            }

            if (string.IsNullOrWhiteSpace(payload))
            {
                return new BadRequestResult();
            }

            JObject parsedPayload;
            try
            {
                parsedPayload = JObject.Parse(payload);
            }
            catch (Newtonsoft.Json.JsonReaderException)
            {
                return new BadRequestResult();
            }

            if (parsedPayload["entry"] is not JArray entries || !entries.Any())
            {
                return new OkResult();
            }

            var changeField = parsedPayload["entry"]?[0]?["changes"]?[0]?["field"]?.ToString();

            if (changeField == "message_template_status_update")
            {
                await ProcessTemplateStatus(parsedPayload, business.BusinessId);
            }
            else if (changeField == "messages")
            {
                await ProcessMessageStatusUpdates(parsedPayload, business.BusinessId);
                
                if (business.SupportsChat)
                {
                    await ProcessMessageEvents(parsedPayload, business.BusinessId);
                }
            }

            return new OkResult();
        }

        private async Task ProcessMessageEvents(JObject payload, string businessId)
        {
            var messageEvents = ExtractMessageEvents(payload, businessId);
            
            foreach (var messageEvent in messageEvents)
            {
                await StoreInboundMessage(messageEvent.RawMessage, businessId);
                await HandleMessageEvent(messageEvent);
            }
        }

        private List<WhatsAppMessageEvent> ExtractMessageEvents(JObject payload, string businessId)
        {
            var events = new List<WhatsAppMessageEvent>();

            if (payload["entry"] is not JArray entries)
                return events;

            foreach (var entry in entries.OfType<JObject>())
            {
                if (entry["changes"] is not JArray changes)
                    continue;

                foreach (var change in changes.OfType<JObject>())
                {
                    if (change["value"] is not JObject value)
                        continue;

                    if (value["messages"] is JArray messages)
                    {
                        foreach (var msg in messages.OfType<JObject>())
                        {
                            var phoneNumber = msg["from"]?.ToString();
                            var messageEvent = CreateMessageEvent(msg, businessId, phoneNumber, payload);
                            if (messageEvent != null)
                            {
                                events.Add(messageEvent);
                            }
                        }
                    }
                }
            }

            return events;
        }

        private WhatsAppMessageEvent CreateMessageEvent(JObject msg, string businessId, string phoneNumber, JObject payload)
        {
            if (string.IsNullOrEmpty(phoneNumber))
                return null;

            var messageType = msg["type"]?.ToString().ToLower();
            var customerName = ExtractCustomerNameFromWebhook(payload, phoneNumber);
            var timestamp = ParseUnixTimestamp(msg["timestamp"]?.ToString());

            var messageEvent = new WhatsAppMessageEvent
            {
                BusinessId = businessId,
                PhoneNumber = phoneNumber,
                CustomerName = customerName,
                Timestamp = timestamp,
                RawMessage = msg
            };

            switch (messageType)
            {
                case "text":
                    messageEvent.EventType = WhatsAppEventType.TextMessage;
                    messageEvent.Content = msg["text"]?["body"]?.ToString();
                    break;

                case "interactive":
                    var interactive = msg["interactive"];
                    var buttonReply = interactive?["button_reply"]?["id"]?.ToString();
                    var listReply = interactive?["list_reply"]?["id"]?.ToString();
                    
                    if (!string.IsNullOrEmpty(buttonReply))
                    {
                        messageEvent.EventType = WhatsAppEventType.InteractiveButton;
                        messageEvent.InteractivePayload = buttonReply;
                        messageEvent.Content = buttonReply;
                    }
                    else if (!string.IsNullOrEmpty(listReply))
                    {
                        messageEvent.EventType = WhatsAppEventType.InteractiveList;
                        messageEvent.InteractivePayload = listReply;
                        messageEvent.Content = listReply;
                    }
                    else
                    {
                        messageEvent.EventType = WhatsAppEventType.InteractiveButton;
                        messageEvent.Content = "[INTERACTIVE]";
                    }
                    break;

                case "image":
                    messageEvent.EventType = WhatsAppEventType.Image;
                    messageEvent.Content = msg["image"]?["caption"]?.ToString() ?? "[IMAGE]";
                    break;

                case "video":
                    messageEvent.EventType = WhatsAppEventType.Video;
                    messageEvent.Content = msg["video"]?["caption"]?.ToString() ?? "[VIDEO]";
                    break;

                case "document":
                    messageEvent.EventType = WhatsAppEventType.Document;
                    messageEvent.Content = msg["document"]?["filename"]?.ToString() ?? "[DOCUMENT]";
                    break;

                case "audio":
                    messageEvent.EventType = WhatsAppEventType.Audio;
                    messageEvent.Content = "[AUDIO]";
                    break;

                case "sticker":
                    messageEvent.EventType = WhatsAppEventType.Sticker;
                    messageEvent.Content = "[STICKER]";
                    break;

                case "order":
                    messageEvent.EventType = WhatsAppEventType.Order;
                    messageEvent.Content = "[ORDER]";
                    break;

                default:
                    messageEvent.EventType = WhatsAppEventType.Unknown;
                    messageEvent.Content = "NON_TEXT";
                    break;
            }

            return messageEvent;
        }

        private async Task HandleMessageEvent(WhatsAppMessageEvent messageEvent)
        {
            switch (messageEvent.EventType)
            {
                case WhatsAppEventType.TextMessage:
                    await OnTextMessageReceived(messageEvent);
                    break;
                    
                case WhatsAppEventType.InteractiveButton:
                    await OnInteractiveButtonClicked(messageEvent);
                    break;
                    
                case WhatsAppEventType.InteractiveList:
                    await OnInteractiveListSelected(messageEvent);
                    break;
                    
                case WhatsAppEventType.Image:
                    await OnImageReceived(messageEvent);
                    break;
                    
                case WhatsAppEventType.Video:
                    await OnVideoReceived(messageEvent);
                    break;
                    
                case WhatsAppEventType.Document:
                    await OnDocumentReceived(messageEvent);
                    break;
                    
                case WhatsAppEventType.Audio:
                    await OnAudioReceived(messageEvent);
                    break;
                    
                case WhatsAppEventType.Sticker:
                    await OnStickerReceived(messageEvent);
                    break;
                    
                case WhatsAppEventType.Order:
                    await OnOrderReceived(messageEvent);
                    break;
                    
                case WhatsAppEventType.FlowSubmitted:
                    await OnFlowSubmitted(messageEvent);
                    break;
                    
                case WhatsAppEventType.Unknown:
                    await OnUnknownMessageType(messageEvent);
                    break;
            }
        }

        protected virtual Task OnTextMessageReceived(WhatsAppMessageEvent messageEvent) => Task.CompletedTask;
        protected virtual Task OnInteractiveButtonClicked(WhatsAppMessageEvent messageEvent) => Task.CompletedTask;
        protected virtual Task OnInteractiveListSelected(WhatsAppMessageEvent messageEvent) => Task.CompletedTask;
        protected virtual Task OnImageReceived(WhatsAppMessageEvent messageEvent) => Task.CompletedTask;
        protected virtual Task OnVideoReceived(WhatsAppMessageEvent messageEvent) => Task.CompletedTask;
        protected virtual Task OnDocumentReceived(WhatsAppMessageEvent messageEvent) => Task.CompletedTask;
        protected virtual Task OnAudioReceived(WhatsAppMessageEvent messageEvent) => Task.CompletedTask;
        protected virtual Task OnStickerReceived(WhatsAppMessageEvent messageEvent) => Task.CompletedTask;
        protected virtual Task OnOrderReceived(WhatsAppMessageEvent messageEvent) => Task.CompletedTask;
        protected virtual Task OnFlowSubmitted(WhatsAppMessageEvent messageEvent) => Task.CompletedTask;
        protected virtual Task OnUnknownMessageType(WhatsAppMessageEvent messageEvent) => Task.CompletedTask;

        private async Task ProcessTemplateStatus(JObject payload, string businessId)
        {
            var value = payload["entry"]?[0]?["changes"]?[0]?["value"];
            var templateId = value?["message_template_id"]?.ToString();
            var templateName = value?["message_template_name"]?.ToString();
            var language = value?["message_template_language"]?.ToString();
            var eventType = value?["event"]?.ToString().ToUpper();

            if (string.IsNullOrEmpty(templateId))
            {
                return;
            }

            var existing = await _db.WhatsAppTemplates
                .FirstOrDefaultAsync(t => t.TemplateId == templateId && t.BusinessId == businessId);

            if (eventType == "DELETED")
            {
                if (existing != null)
                {
                    _db.WhatsAppTemplates.Remove(existing);
                }
            }
            else
            {
                if (existing == null)
                {
                    _db.WhatsAppTemplates.Add(new WhatsAppTemplate
                    {
                        TemplateId = templateId,
                        BusinessId = businessId,
                        TemplateName = templateName,
                        Status = eventType,
                        Language = language,
                        CreatedAt = DateTime.UtcNow,
                    });
                }
                else
                {
                    existing.Status = eventType;
                    if (!string.IsNullOrEmpty(templateName))
                    {
                        existing.TemplateName = templateName;
                    }
                    if (!string.IsNullOrEmpty(language))
                    {
                        existing.Language = language;
                    }
                }
            }
            await _db.SaveChangesAsync();
        }

        private async Task ProcessMessageStatusUpdates(JObject payload, string businessId)
        {
            if (payload["entry"] is not JArray entries)
            {
                return;
            }

            foreach (var entry in entries.OfType<JObject>())
            {
                if (entry["changes"] is not JArray changes)
                {
                    continue;
                }

                foreach (var change in changes.OfType<JObject>())
                {
                    if (change["value"] is not JObject value)
                    {
                        continue;
                    }

                    if (value["statuses"] != null)
                    {
                        await ProcessMessageStatus(value, businessId);
                    }
                }
            }
        }

        public async Task StoreInboundMessage(JObject msg, string businessId)
        {
            var providerMessageId = msg["id"]?.ToString().Trim();
            
            if (string.IsNullOrEmpty(providerMessageId))
            {
                return;
            }

            var existing = await _db.WhatsAppMessages
                .FirstOrDefaultAsync(m => m.ProviderMessageId == providerMessageId &&
                    m.BusinessId == businessId
            );

            if (existing == null)
            {
                var message = new WhatsAppMessage
                {
                    BusinessId = businessId,
                    MessageId = Guid.NewGuid().ToString(),
                    ProviderMessageId = providerMessageId,
                    PhoneNumber = msg["from"]?.ToString().Trim(),
                    Content = GetMessageContent(msg),
                    Timestamp = ParseUnixTimestamp(msg["timestamp"]?.ToString()),
                    Status = "RECEIVED",
                    Direction = "INBOUND",
                };
                await _db.WhatsAppMessages.AddAsync(message);
                await _db.SaveChangesAsync();
            }
        }

        private async Task ProcessMessageStatus(JObject value, string businessId)
        {
            var statuses = value?["statuses"] as JArray ?? new JArray();
            foreach (var status in statuses.OfType<JObject>())
            {
                var providerMessageId = status["id"]?.ToString().Trim();
                var existingMessage = await _db.WhatsAppMessages
                    .FirstOrDefaultAsync(m =>
                        m.ProviderMessageId == providerMessageId &&
                        m.BusinessId == businessId
                    );
                if (existingMessage != null)
                {
                    existingMessage.Status = status["status"]?.ToString().ToUpper() ?? "UNKNOWN";
                    existingMessage.Timestamp = ParseUnixTimestamp(status["timestamp"]?.ToString());
                    existingMessage.ErrorCode = status["errors"]?[0]?["code"]?.ToString();
                    existingMessage.ErrorTitle = status["errors"]?[0]?["title"]?.ToString();
                }
            }
            
            await _db.SaveChangesAsync();
        }

        protected static DateTime ParseUnixTimestamp(string ts)
        {
            if (string.IsNullOrEmpty(ts) || !long.TryParse(ts, out var num) || num <= 0)
            {
                return DateTime.MinValue;
            }
            
            var utcDateTime = DateTimeOffset.FromUnixTimeSeconds(num).UtcDateTime;
            return utcDateTime;
        }

        protected static string GetMessageContent(JToken msg)
        {
            var type = msg["type"]?.ToString().ToLower();
            return type switch
            {
                "text" => msg["text"]?["body"]?.ToString(),
                "interactive" => GetInteractiveContent(msg["interactive"]),
                "image" => msg["image"]?["caption"]?.ToString() ?? "[IMAGE]",
                "video" => msg["video"]?["caption"]?.ToString() ?? "[VIDEO]",
                "document" => msg["document"]?["filename"]?.ToString() ?? "[DOCUMENT]",
                "audio" => "[AUDIO]",
                "sticker" => "[STICKER]",
                _ => "NON_TEXT"
            };
        }

        protected static string GetInteractiveContent(JToken interactive)
        {
            return interactive?["button_reply"]?["id"]?.ToString()
                ?? interactive?["list_reply"]?["id"]?.ToString()
                ?? "[INTERACTIVE]";
        }

        protected static string ExtractCustomerNameFromWebhook(JObject webhookPayload, string phoneNumber)
        {
            try
            {
                if (webhookPayload["entry"]?[0]?["changes"]?[0]?["value"]?["contacts"] is JArray contacts)
                {
                    foreach (var contact in contacts)
                    {
                        var contactPhone = contact["wa_id"]?.ToString();
                        var contactName = contact["profile"]?["name"]?.ToString();

                        if (contactPhone == phoneNumber && !string.IsNullOrEmpty(contactName))
                        {
                            return contactName;
                        }
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}