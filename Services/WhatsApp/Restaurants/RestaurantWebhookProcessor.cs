using System;
using System.Threading.Tasks;
using FusionComms.Data;
using FusionComms.DTOs.WhatsApp;
using FusionComms.Services.WhatsApp;
using FusionComms.Services.WhatsApp.Restaurants;


namespace FusionComms.Services.WhatsApp.Restaurants
{
    public class RestaurantWebhookProcessor : WhatsAppWebhookProcessor
    {
        private readonly OrderFlowEngine _orderFlowEngine;
        private readonly OrderSessionManager _sessionManager;
        private readonly OrderStateManager _stateManager;
        private readonly WhatsAppMessagingService _whatsAppMessaging;

        public RestaurantWebhookProcessor(
            AppDbContext dbContext,
            WhatsAppMessagingService whatsAppMessaging,
            OrderFlowEngine orderFlowEngine,
            OrderSessionManager sessionManager,
            OrderStateManager stateManager)
            : base(dbContext)
        {
            _orderFlowEngine = orderFlowEngine;
            _sessionManager = sessionManager;
            _stateManager = stateManager;
            _whatsAppMessaging = whatsAppMessaging;
        }

        protected override async Task OnTextMessageReceived(WhatsAppMessageEvent messageEvent)
        {
            await _orderFlowEngine.ProcessMessage(
                messageEvent.BusinessId, 
                messageEvent.PhoneNumber, 
                messageEvent.Content, 
                messageEvent.CustomerName
            );
            
            await CleanupSessions();
        }

        protected override async Task OnInteractiveButtonClicked(WhatsAppMessageEvent messageEvent)
        {
            await _orderFlowEngine.ProcessMessage(
                messageEvent.BusinessId,
                messageEvent.PhoneNumber,
                messageEvent.InteractivePayload,
                messageEvent.CustomerName
            );
            
            await CleanupSessions();
        }

        protected override async Task OnInteractiveListSelected(WhatsAppMessageEvent messageEvent)
        {
            await _orderFlowEngine.ProcessMessage(
                messageEvent.BusinessId,
                messageEvent.PhoneNumber,
                messageEvent.InteractivePayload,
                messageEvent.CustomerName
            );
            
            await CleanupSessions();
        }

        protected override async Task OnFlowSubmitted(WhatsAppMessageEvent messageEvent)
        {
            // Parse flow response and map to session
            await ProcessFlowSubmission(messageEvent);
            await CleanupSessions();
        }

        private async Task ProcessFlowSubmission(WhatsAppMessageEvent messageEvent)
        {
            try
            {
                var session = await _sessionManager.GetExistingSession(messageEvent.BusinessId, messageEvent.PhoneNumber);
                if (session == null || session.CurrentState != "FLOW_IN_PROGRESS")
                {
                    return;
                }

                // Assuming flow payload contains form data
                var flowData = messageEvent.RawMessage?["flow"]?["data"];
                if (flowData != null)
                {
                    // Map flow fields to session (adjust based on actual flow JSON structure)
                    if (flowData["Full_Address_e27e24"] != null)
                    {
                        session.DeliveryAddress = flowData["Full_Address_e27e24"].ToString();
                    }
                    if (flowData["Telephone_Number_4fdd78"] != null)
                    {
                        session.DeliveryContactPhone = flowData["Telephone_Number_4fdd78"].ToString();
                    }
                    if (flowData["_Delivery_Area_060c5c"] != null)
                    {
                        // Map area selection to DeliveryChargeId if needed
                        // session.DeliveryChargeId = MapAreaToChargeId(flowData["_Delivery_Area_060c5c"].ToString());
                    }

                    session.CurrentState = "DELIVERY_METHOD"; // Reset to proceed normally
                    await _sessionManager.UpdateSession(session);

                    // Continue checkout flow
                    await _stateManager.ProceedToCheckoutFlow(session);
                }
            }
            catch (Exception ex)
            {
                // Log error and fallback
                await _whatsAppMessaging.SendTextMessageAsync(
                    messageEvent.BusinessId,
                    messageEvent.PhoneNumber,
                    "❌ Error processing delivery information. Please try again.");
            }
        }

        protected override async Task OnOrderReceived(WhatsAppMessageEvent messageEvent)
        {
            await _orderFlowEngine.ProcessOrderMessage(
                messageEvent.BusinessId, 
                messageEvent.PhoneNumber, 
                messageEvent.RawMessage["order"]
            );
            
            await CleanupSessions();
        }

        protected override async Task OnUnknownMessageType(WhatsAppMessageEvent messageEvent)
        {
            await _whatsAppMessaging.SendTextMessageAsync(
                messageEvent.BusinessId,
                messageEvent.PhoneNumber,
                "I'm sorry, I don't understand that type of message. Please send text or use the provided options."
            );
        }

        private async Task CleanupSessions()
        {
            await _sessionManager.CleanupOldSessions();
            await _sessionManager.CleanupCancelledSessions();
        }
    }
}