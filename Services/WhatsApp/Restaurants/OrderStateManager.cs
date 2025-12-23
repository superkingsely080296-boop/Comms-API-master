using FusionComms.Data;
using FusionComms.Utilities;
using FusionComms.DTOs.WhatsApp;
using FusionComms.Entities.WhatsApp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace FusionComms.Services.WhatsApp.Restaurants
{
    public class OrderStateManager
    {
        private readonly OrderService _orderService;
        private readonly WhatsAppMessagingService _messagingService;
        private readonly OrderUIManager _uiManager;
        private readonly OrderCartManager _cartManager;
        private readonly OrderValidationService _validationService;
        private readonly AppDbContext _db;
        private readonly ProfileManager _profileManager;
        private readonly OrderSessionManager _sessionManager;
        private readonly WhatsAppProfileService _whatsAppProfileService;

        public OrderStateManager(
            OrderService orderService,
            WhatsAppMessagingService messagingService,
            OrderUIManager uiManager,
            OrderCartManager cartManager,
            OrderValidationService validationService,
            AppDbContext dbContext,
            ProfileManager profileManager,
            WhatsAppProfileService whatsAppProfileService,
            OrderSessionManager sessionManager)
        {
            _orderService = orderService;
            _messagingService = messagingService;
            _uiManager = uiManager;
            _cartManager = cartManager;
            _validationService = validationService;
            _db = dbContext;
            _profileManager = profileManager;
            _whatsAppProfileService = whatsAppProfileService;
            _sessionManager = sessionManager;
        }

        public async Task HandleLocationSelection(OrderSession session, string message)
        {
            if (string.IsNullOrEmpty(message) || message == "NON_TEXT" || message == "START_ORDER")
            {
                await _uiManager.ShowLocationSelection(session, session.CustomerName);
                return;
            }

            var revenueCenters = await _orderService.GetRevenueCenters(session.BusinessId);
            var revenueCenter = revenueCenters.FirstOrDefault(rc => rc.Id == message);
            
            if (revenueCenter == null)
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Invalid location selected.\n\n" +
                    "Please choose from the list below.");
                await _uiManager.ShowLocationSelection(session, session.CustomerName);
                return;
            }

            if (revenueCenter.Restaurant != null && 
                !_validationService.IsLocationOpen(revenueCenter.Restaurant.StartTime, revenueCenter.Restaurant.EndTime))
            {
                session.RevenueCenterId = revenueCenter.Id;
                var startWat = TimeZoneHelper.ToWat(revenueCenter.Restaurant.StartTime);
                
                var closedMessage = $"⏰ *We're currently closed*\n\n" +
                                $"📍 {revenueCenter.Name}\n" +
                                $"🕐 We'll process your order at *{startWat:hh:mm tt}*\n\n" +
                                "Would you like to continue?";

                await _messagingService.SendInteractiveMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    closedMessage,
                    new List<WhatsAppButton>
                    {
                        new() { Text = "✅ Yes, Continue", Payload = "CONFIRM_CLOSED_YES" },
                        new() { Text = "❌ No, Cancel", Payload = "CONFIRM_CLOSED_NO" }
                    });

                session.CurrentState = "CONFIRM_CLOSED_RESTAURANT";
                await _sessionManager.UpdateSession(session);
                return;
            }

            session.RevenueCenterId = revenueCenter.Id;
            session.TaxExclusive = revenueCenter.Restaurant?.TaxExclusive ?? false;
            session.HelpEmail = revenueCenter.HelpEmail;
            session.HelpPhoneNumber = revenueCenter.HelpPhoneNumber;

            if (!revenueCenter.PickupAvailable)
            {
                session.DeliveryMethod = "Delivery";
            }

            session.CurrentState = "ITEM_SELECTION";

            DateTime? startUtc = revenueCenter.Restaurant?.StartTime;
            DateTime? endUtc = revenueCenter.Restaurant?.EndTime;

            string startWatFormatted = startUtc.HasValue ? TimeZoneHelper.ToWat(startUtc.Value).ToString("hh:mm tt") : "N/A";
            string endWatFormatted = endUtc.HasValue ? TimeZoneHelper.ToWat(endUtc.Value).ToString("hh:mm tt") : "N/A";

            var successMessage = $"📍 {revenueCenter.Name}\n" +
                                $"🏠 {revenueCenter.Address}, {revenueCenter.State}\n" +
                                $"🕐 Operating Hours: {startWatFormatted} - {endWatFormatted}";

            await _messagingService.SendTextMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                successMessage);

            await _uiManager.ShowMainMenu(session.BusinessId, session.PhoneNumber, "Click 'View items' to add items to your cart:");
        }

        public async Task HandleDeliveryMethod(OrderSession session, OrderCart cart, string message)
        {
            var business = await _orderService.GetBusinessAsync(session.BusinessId);
            var revenueCenters = await _orderService.GetRevenueCenters(session.BusinessId);
            var revenueCenter = revenueCenters.FirstOrDefault(rc => rc.Id == session.RevenueCenterId);
            
            switch (message)
            {
                case "DELIVERY":
                    session.DeliveryMethod = "Delivery";
                    session.CurrentState = "DELIVERY_LOCATION_SELECTION";
                    await _uiManager.ShowDeliveryLocationSelection(session);
                    break;
                case "PICKUP":
                    session.DeliveryMethod = "Pickup";
                    var sessionCart = _cartManager.DeserializeCart(session.CartData);
                    if (sessionCart.Items.Any())
                    {
                        session.CurrentState = "COLLECT_NOTES";
                        await SendSpecialInstructionsPrompt(session);
                    }
                    else
                    {
                        session.CurrentState = "ITEM_SELECTION";
                        await _uiManager.ShowMainMenu(session.BusinessId, session.PhoneNumber);
                    }
                    break;
                default:
                    await _messagingService.SendTextMessageAsync(
                        session.BusinessId,
                        session.PhoneNumber,
                        "❌ Invalid delivery option.\n\n" +
                        "Please select from the buttons below." +
                        MessageFormattingHelper.FormatHelpContactFooter(session));
                    await _uiManager.AskDeliveryMethod(session);
                    break;
            }
        }

        public async Task HandleClosedRestaurantConfirmation(OrderSession session, string message)
        {
            if (message == "CONFIRM_CLOSED_YES")
            {
                var revenueCenters = await _orderService.GetRevenueCenters(session.BusinessId);
                var revenueCenter = revenueCenters.FirstOrDefault(rc => rc.Id == session.RevenueCenterId);
                
                if (revenueCenter != null)
                {
                    session.TaxExclusive = revenueCenter.Restaurant?.TaxExclusive ?? false;
                    session.HelpEmail = revenueCenter.HelpEmail;
                    session.HelpPhoneNumber = revenueCenter.HelpPhoneNumber;
                    if (!revenueCenter.PickupAvailable) session.DeliveryMethod = "Delivery";
                    
                    session.CurrentState = "ITEM_SELECTION";
                    await _uiManager.ShowMainMenu(session.BusinessId, session.PhoneNumber, "Click 'View items' to add items to your cart:");
                }
            }
            else if (message == "CONFIRM_CLOSED_NO")
            {
                await HandleCancelRequest(session);
            }
            else
            {
                await _messagingService.SendTextMessageAsync(session.BusinessId, session.PhoneNumber, "Please select Yes to continue or Cancel.");
            }
        }

        public async Task HandleClosedDeliveryConfirmation(OrderSession session, string message)
        {
            switch (message)
            {
                case "PROCEED_DELIVERY":
                    session.DeliveryMethod = "Delivery";
                    session.CurrentState = "DELIVERY_LOCATION_SELECTION";
                    await _uiManager.ShowDeliveryLocationSelection(session, skipHoursCheck: true);
                    break;
                case "SWITCH_TO_PICKUP":
                    session.DeliveryMethod = "Pickup";
                    session.CurrentState = "COLLECT_NOTES";
                    await SendSpecialInstructionsPrompt(session);
                    break;
                case "CANCEL_ORDER":
                    await HandleCancelRequest(session);
                    break;
                default:
                    await _messagingService.SendTextMessageAsync(session.BusinessId, session.PhoneNumber, "Please select an option above.");
                    break;
            }
        }

        public async Task ProceedToCheckoutFlow(OrderSession session)
        {
            var revenueCenters = await _orderService.GetRevenueCenters(session.BusinessId);
            var revenueCenter = revenueCenters.FirstOrDefault(rc => rc.Id == session.RevenueCenterId);

            if (revenueCenter != null && !revenueCenter.PickupAvailable)
            {
                session.DeliveryMethod = "Delivery";
            }

            // If flow has provided delivery data, skip to notes or confirmation
            if (session.DeliveryMethod == "Delivery" && !string.IsNullOrEmpty(session.DeliveryAddress) && !string.IsNullOrEmpty(session.DeliveryContactPhone))
            {
                var currentCart = _cartManager.DeserializeCart(session.CartData);
                if (!string.IsNullOrEmpty(session.Notes))
                {
                    session.CurrentState = "ORDER_CONFIRMATION";
                    await _uiManager.ShowOrderSummary(session, currentCart);
                }
                else
                {
                    session.CurrentState = "COLLECT_NOTES";
                    await SendSpecialInstructionsPrompt(session);
                }
                return;
            }

            if (string.IsNullOrEmpty(session.DeliveryMethod))
            {
                session.CurrentState = "DELIVERY_METHOD";
                await _uiManager.AskDeliveryMethod(session);
            }
            else if (session.DeliveryMethod == "Delivery" && string.IsNullOrEmpty(session.DeliveryChargeId))
            {
                session.CurrentState = "DELIVERY_LOCATION_SELECTION";
                await _uiManager.ShowDeliveryLocationSelection(session);
            }
            else if (session.DeliveryMethod == "Delivery" && string.IsNullOrEmpty(session.DeliveryAddress))
            {
                session.CurrentState = "DELIVERY_ADDRESS";
                var hasAddresses = await _profileManager.HasAddressesAsync(session.BusinessId, session.PhoneNumber);
                if (hasAddresses)
                {
                    await _profileManager.ShowSavedAddressesForOrderAsync(session.BusinessId, session.PhoneNumber);
                }
                else
                {
                    await _messagingService.SendTextMessageAsync(
                        session.BusinessId,
                        session.PhoneNumber,
                        "📝 Please enter your delivery address:");
                }
            }
            else if (session.DeliveryMethod == "Delivery")
            {
                var hasContactPhone = await _profileManager.HasContactPhoneAsync(session.BusinessId, session.PhoneNumber);
                
                if (!hasContactPhone)
                {
                    session.CurrentState = "DELIVERY_CONTACT_PHONE";
                    await _messagingService.SendTextMessageAsync(
                        session.BusinessId,
                        session.PhoneNumber,
                        MessageConstants.DeliveryContactPhonePrompt ?? 
                        "📱 *Contact Phone Required*\n\nPlease enter a phone number to contact you for delivery:\n\nExample: 08012345678");
                    return;
                }

                var currentCart = _cartManager.DeserializeCart(session.CartData);
                if (!string.IsNullOrEmpty(session.Notes))
                {
                    session.CurrentState = "ORDER_CONFIRMATION";
                    await _uiManager.ShowOrderSummary(session, currentCart);
                }
                else
                {
                    session.CurrentState = "COLLECT_NOTES";
                    await SendSpecialInstructionsPrompt(session);
                }
            }
            else 
            {
                var currentCart = _cartManager.DeserializeCart(session.CartData);
                if (!string.IsNullOrEmpty(session.Notes))
                {
                    session.CurrentState = "ORDER_CONFIRMATION";
                    await _uiManager.ShowOrderSummary(session, currentCart);
                }
                else
                {
                    session.CurrentState = "COLLECT_NOTES";
                    await SendSpecialInstructionsPrompt(session);
                }
            }
        }

        public async Task HandleDeliveryContactPhone(OrderSession session, OrderCart cart, string message)
        {
            string normalized = NormalizeContactPhone(message);
            if (string.IsNullOrEmpty(normalized))
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Invalid phone format. Please enter a valid contact number (e.g., +2348012345678 or 08012345678)." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));
                return;
            }

            var saved = await _whatsAppProfileService.SaveContactPhoneAsync(session.BusinessId, session.PhoneNumber, normalized);
            if (!saved)
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Failed to save phone number. Please try again." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));
                return;
            }

            // await _messagingService.SendTextMessageAsync(
            //     session.BusinessId,
            //     session.PhoneNumber,
            //     "✅ Contact phone saved!");

            if (!string.IsNullOrEmpty(session.DeliveryAddress))
            {
                var sessionCart = _cartManager.DeserializeCart(session.CartData);
                if (sessionCart.Items.Any())
                {
                    session.CurrentState = "COLLECT_NOTES";
                    await SendSpecialInstructionsPrompt(session);

                }
                else
                {
                    session.CurrentState = "ITEM_SELECTION";
                    await _uiManager.ShowMainMenu(session.BusinessId, session.PhoneNumber);
                }
            }
            else
            {
                session.CurrentState = "DELIVERY_LOCATION_SELECTION";
                await _uiManager.ShowDeliveryLocationSelection(session);
            }
        }

        private string NormalizeContactPhone(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var digits = new string(input.Where(char.IsDigit).ToArray());
            if (digits.Length < 7 || digits.Length > 15) return null;
            if (input.Trim().StartsWith("+"))
            {
                return "+" + digits;
            }
            if (input.Trim().StartsWith("0"))
            {
                return digits.StartsWith("0") ? digits : "0" + digits;
            }
            return digits;
        }

        public async Task HandleDeliveryLocationSelection(OrderSession session, OrderCart cart, string message)
        {
            if (message == "LOCATION_NOT_LISTED")
            {
                session.DeliveryMethod = "Pickup";
                
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "✅ Switched to pickup. " +
                    "No delivery address needed.");
                
                var sessionCart = _cartManager.DeserializeCart(session.CartData);
                if (sessionCart.Items.Any())
                {
                    session.CurrentState = "COLLECT_NOTES";
                    await SendSpecialInstructionsPrompt(session);
                }
                else
                {
                    session.CurrentState = "ITEM_SELECTION";
                    await _uiManager.ShowMainMenu(session.BusinessId, session.PhoneNumber);
                }
                return;
            }

            var business = await _orderService.GetBusinessAsync(session.BusinessId);
            var deliveryCharges = await _orderService.GetChargesAsync(business.RestaurantId, session.RevenueCenterId, "Delivery");
            var selectedCharge = deliveryCharges.FirstOrDefault(c => c.Id == message);
            var validationTime = DateTime.UtcNow;
            
            if (selectedCharge == null || !selectedCharge.IsActive || selectedCharge.ExpiryDate <= validationTime)
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Invalid delivery area.\n\n" +
                    "Please select from the list below." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));
                await _uiManager.ShowDeliveryLocationSelection(session);
                return;
            }

            session.DeliveryChargeId = selectedCharge.Id;
            session.CurrentState = "DELIVERY_ADDRESS";
            
            var hasAddresses = await _profileManager.HasAddressesAsync(session.BusinessId, session.PhoneNumber);
            
            if (hasAddresses)
            {
                await _profileManager.ShowSavedAddressesForOrderAsync(session.BusinessId, session.PhoneNumber);
            }
            else
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "📝 Please enter your delivery address:");
            }
        }

        public async Task HandleDeliverySwitchConfirmation(OrderSession session, OrderCart cart, string message)
        {
            if (message == "SWITCH_TO_PICKUP_YES")
            {
                session.DeliveryMethod = "Pickup";

                var sessionCart = _cartManager.DeserializeCart(session.CartData);
                if (sessionCart.Items.Count > 0)
                {
                    session.CurrentState = "COLLECT_NOTES";
                        await SendSpecialInstructionsPrompt(session);
                }
                else
                {
                    session.CurrentState = "ITEM_SELECTION";
                    await _uiManager.ShowMainMenu(session.BusinessId, session.PhoneNumber);
                }
                return;
            }

            if (message == "SWITCH_TO_PICKUP_NO")
            {
                session.CurrentState = "CANCELLED";
                await _sessionManager.DeleteSession(session.BusinessId, session.PhoneNumber);
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Order closed.\n\nYou can start a new order anytime." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));
                return;
            }

            await _messagingService.SendTextMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                "Please choose one of the options above.");
        }

        public async Task HandleDeliveryAddress(OrderSession session, OrderCart cart, string message)
        {
            if (!_validationService.IsValidAddress(message))
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Address too short.\n\n" +
                    "Please enter at least 10 characters." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));
                return;
            }

            session.DeliveryAddress = message.Trim();

            var buttons = new List<WhatsAppButton>
            {
                new() { Text = "✅ Yes, save it", Payload = "SAVE_ADDRESS_YES" },
                new() { Text = "👍 No, thanks", Payload = "SAVE_ADDRESS_NO" }
            };

            await _messagingService.SendInteractiveMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                "Save for next time?\n\nUse \"Manage Profile\" to edit anytime.",
                buttons);

            session.CurrentState = "ADDRESS_SAVE_PROMPT";
        }

        public async Task HandleItemSelection(OrderSession session, OrderCart cart, string message)
        {
            cart = _cartManager.DeserializeCart(session.CartData);

            if (_validationService.IsDiscountRequest(message))
            {
                await HandleDiscountCodeRequest(session);
                return;
            }

            if (message.Equals("SEARCH", StringComparison.OrdinalIgnoreCase) || 
                message.Equals("🔍 Search Menu", StringComparison.OrdinalIgnoreCase))
            {
                session.CurrentState = "SEARCH";
                await _uiManager.ShowSearchPrompt(session.BusinessId, session.PhoneNumber);
                return;
            }

            if (message.Equals("FULL_MENU", StringComparison.OrdinalIgnoreCase) || 
                message.Equals("📖 Browse Menu", StringComparison.OrdinalIgnoreCase))
            {
                await _uiManager.ShowCategoriesList(session);
                return;
            }

            if (!_validationService.AllowsCatalogInteraction(session.CurrentState))
            {
                await HandleInvalidItemSelection(session);
                return;
            }

            if (!_validationService.HasCompletedRequiredSteps(session, "ADD_TO_CART"))
            {
                await HandleIncompleteFlowForItemSelection(session);
                return;
            }

            var business = await _orderService.GetBusinessAsync(session.BusinessId);
            var revenueCenters = await _orderService.GetRevenueCenters(session.BusinessId);
            var revenueCenter = revenueCenters.FirstOrDefault(rc => rc.Id == session.RevenueCenterId);
            
            if (message.Equals("VIEW_MORE_CATEGORIES", StringComparison.OrdinalIgnoreCase))
            {
                await _uiManager.ShowCategoriesList(session);
                return;
            }
            if (message.StartsWith("CAT_PAGE_", StringComparison.OrdinalIgnoreCase))
            {
                var pageStr = message.Substring("CAT_PAGE_".Length);
                if (!int.TryParse(pageStr, out var page) || page < 1) page = 1;
                await _uiManager.ShowCategoriesList(session, page);
                return;
            }
            if (message.StartsWith("SUBCAT_PAGE_", StringComparison.OrdinalIgnoreCase))
            {
                var pageStr = message.Substring("SUBCAT_PAGE_".Length);
                if (!int.TryParse(pageStr, out var subPage) || subPage < 1) subPage = 1;
                var current = session.CurrentCategoryGroup;
                if (string.IsNullOrEmpty(current))
                {
                    await _uiManager.ShowCategoriesList(session, 1);
                    return;
                }
                if (current.StartsWith("SET:"))
                {
                    await _uiManager.ShowCategoryProducts(session, current.Substring(4), isGroupingId: false, subPage: subPage);
                }
                else
                {
                    await _uiManager.ShowCategoryProducts(session, current, isGroupingId: true, subPage: subPage);
                }
                return;
            }
            if (message.StartsWith("CAT_SET_", StringComparison.OrdinalIgnoreCase))
            {
                var setId = message.Substring("CAT_SET_".Length);
                await _uiManager.ShowCategoryProducts(session, setId, isGroupingId: false);
                return;
            }
            if (message.StartsWith("CAT_", StringComparison.OrdinalIgnoreCase))
            {
                var selectedGroupingId = message.Substring("CAT_".Length);
                await _uiManager.ShowCategoryProducts(session, selectedGroupingId, isGroupingId: true);
                return;
            }
            if (message.StartsWith("SUBCAT_", StringComparison.OrdinalIgnoreCase))
            {
                var subcat = message["SUBCAT_".Length..];
                var rev = session.RevenueCenterId;

                List<string> allowedSetIds;
                var current = session.CurrentCategoryGroup;
                if (!string.IsNullOrEmpty(current))
                {
                    if (current.StartsWith("SET:"))
                    {
                        allowedSetIds = new List<string> { current.Substring(4) };
                    }
                    else
                    {
                        var grouping = await _orderService.GetProductSetGroupingById(session.BusinessId, current);
                        allowedSetIds = _orderService.ParseGroupingSetIds(grouping);
                    }
                }
                else
                {
                    await _uiManager.ShowCategoriesList(session);
                    return;
                }

                allowedSetIds = allowedSetIds?.Take(10).ToList() ?? new List<string>();

                string categoryName;
                if (current.StartsWith("SET:"))
                {
                    var setId = current.Substring(4);
                    var sets = await _orderService.GetProductSets(session.BusinessId);
                    categoryName = sets.FirstOrDefault(s => s.SetId == setId)?.Name ?? "Our Menu";
                }
                else
                {
                    var grouping = await _orderService.GetProductSetGroupingById(session.BusinessId, current);
                    categoryName = grouping?.GroupName ?? "Our Menu";
                }
                var headerText = $"{categoryName} • {subcat}";

                var resp = await _messagingService.SendFullCatalogMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    headerText: headerText,
                    bodyText: "Click 'View items' to add items to your cart:",
                    revenueCenterId: rev,
                    allowedSetIds: allowedSetIds,
                    subcategory: subcat);

                if (resp.Success)
                {
                    session.CurrentMenuLevel = "products";
                    session.CurrentSubcategoryGroup = subcat;
                    
                    var backOptions = new List<WhatsAppButton>
                    {
                        new() { Text = "⬅️ Back", Payload = "BACK_SUBCATEGORIES" },
                    };

                    if (cart.Items.Count > 0)
                    {
                        backOptions.Add(new() { Text = "🛒 Checkout", Payload = "PROCEED_CHECKOUT" });
                    }

                    var navResp = await _messagingService.SendInteractiveMessageAsync(
                        session.BusinessId,
                        session.PhoneNumber,
                        "Use the buttons below to navigate:",
                        backOptions);
                }
                else
                {
                    await _uiManager.ShowCategoriesList(session);
                }
                return;
            }

            if (message.Equals("BACK_CATEGORIES", StringComparison.OrdinalIgnoreCase))
            {
                await _uiManager.ShowCategoriesList(session);
                return;
            }
            if (message.Equals("BACK_SUBCATEGORIES", StringComparison.OrdinalIgnoreCase))
            {
                var current = session.CurrentCategoryGroup;
                if (!string.IsNullOrEmpty(current))
                {
                    if (current.StartsWith("SET:"))
                    {
                        await _uiManager.ShowCategoryProducts(session, current[4..], isGroupingId: false);
                    }
                    else
                    {
                        await _uiManager.ShowCategoryProducts(session, current, isGroupingId: true);
                    }
                }
                else
                {
                    await _uiManager.ShowCategoriesList(session);
                }
                return;
            }
            if (message.Equals("BACK_TO_MAIN", StringComparison.OrdinalIgnoreCase))
            {
                await _uiManager.ShowMainMenu(session.BusinessId, session.PhoneNumber, "Click 'View items' to add items to your cart:");
                return;
            }
            if (message.Equals("ADD_MORE", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(session.CurrentCategoryGroup))
                {
                    await _uiManager.ShowMainMenu(session.BusinessId, session.PhoneNumber, "Click 'View items' to add items to your cart:");
                    return;
                }
                
                if (session.CurrentMenuLevel == "products")
                {
                    await _uiManager.ShowCategoriesList(session);
                    return;
                }
                
                if (!string.IsNullOrEmpty(session.CurrentSubcategoryGroup))
                {
                    if (session.CurrentCategoryGroup.StartsWith("SET:"))
                    {
                        await _uiManager.ShowCategoryProducts(session, session.CurrentCategoryGroup[4..], isGroupingId: false);
                    }
                    else
                    {
                        await _uiManager.ShowCategoryProducts(session, session.CurrentCategoryGroup, isGroupingId: true);
                    }
                    return;
                }
                
                var current = session.CurrentCategoryGroup;
                if (!string.IsNullOrEmpty(current))
                {
                    if (current.StartsWith("SET:"))
                    {
                        await _uiManager.ShowCategoryProducts(session, current.Substring(4), isGroupingId: false);
                    }
                    else
                    {
                        await _uiManager.ShowCategoryProducts(session, current, isGroupingId: true);
                    }
                }
                else
                {
                    await _uiManager.ShowCategoriesList(session);
                }
                return;
            }
            if (message.Equals("BROWSE_OTHERS", StringComparison.OrdinalIgnoreCase))
            {
                await _uiManager.ShowCategoriesList(session);
                return;
            }
            if (message.Equals("PROCEED_CHECKOUT", StringComparison.OrdinalIgnoreCase))
            {
                await ProceedToCheckoutFlow(session);
                    return;
            }

            if (message.Equals("BACK_TO_SUMMARY", StringComparison.OrdinalIgnoreCase)
                || message.Trim().Equals("ORDER SUMMARY", StringComparison.OrdinalIgnoreCase)
                || message.Trim().Equals("🧾 ORDER SUMMARY", StringComparison.OrdinalIgnoreCase))
            {
                var currentCart = _cartManager.DeserializeCart(session.CartData);
                await _uiManager.ShowOrderSummary(session, currentCart);
                return;
            }

            if (message.Equals("EDIT_ORDER", StringComparison.OrdinalIgnoreCase) || message.Equals("REMOVE_ITEM", StringComparison.OrdinalIgnoreCase))
            {
                var currentCart = _cartManager.DeserializeCart(session.CartData);
                if (!currentCart.Items.Any())
                {
                    await _messagingService.SendTextMessageAsync(
                        session.BusinessId,
                        session.PhoneNumber,
                        "🛒 Your cart is empty. Add some items first.");
                    await _uiManager.ShowMainMenu(session.BusinessId, session.PhoneNumber, "Click 'View items' to add items to your cart:");
                    return;
                }

                session.CurrentState = "EDIT_ORDER";
                await _uiManager.ShowEditOrderMenu(session, currentCart);
                return;
            }

            if (message.Length > 50 || message.Contains(" ") || (!_validationService.IsValidItemId(message) && !_validationService.IsValidButtonCommand(message)))
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "ℹ️ After browsing our menu, tap the '+' button next to any item to add it to your cart. Thank you!");
                return;
            }
            

            var catalogItems = await _orderService.GetItemsAsync(business.RestaurantId, session.RevenueCenterId, new List<string> { message });
            var selectedItem = catalogItems.FirstOrDefault(i => i.Id == message);

            if (selectedItem == null)
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Invalid item selected.\n\n" +
                    "Use the catalog menu below to browse items." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));
                await _uiManager.ShowMainMenu(session.BusinessId, session.PhoneNumber);
                return;
            }

            string groupingIdForItem = session.IsEditing ? session.EditingGroupId : Guid.NewGuid().ToString();
            string packIdForItem = session.CurrentPackId ?? "pack1";

            List<CartItem> childrenToPreserve = new();
            if (session.IsEditing)
            {
                childrenToPreserve = _cartManager.GetChildrenByGroupId(cart, session.EditingGroupId);
                _cartManager.RemoveItemsByGroupId(cart, session.EditingGroupId);
                _cartManager.UpdateChildrenGroupId(childrenToPreserve, groupingIdForItem);
            }
            
            bool hasToppings = !string.IsNullOrEmpty(selectedItem?.ToppingClassId);

            if (selectedItem.HasRecipeParents && selectedItem.RecipeParents.Any())
            {
                var existingItems = cart.Items
                    .Where(i => i.GroupingId != groupingIdForItem)
                    .ToList();

                var recipeParent = selectedItem.RecipeParents.First();
                if (recipeParent.ItemParent?.ItemsInParent?.Any() == true)
                {
                    var pendingParents = JsonConvert.DeserializeObject<List<PendingParent>>(session.PendingParents ?? "[]");
                    int userRequestedQty = 1;
                    int setsRequired = recipeParent.Quantity;

                    var newItems = new List<CartItem>();
                    string itemParentId = recipeParent.ItemParent.Id;

                    for (int setIndex = 0; setIndex < userRequestedQty; setIndex++)
                    {
                        newItems.Add(new CartItem
                        {
                            ItemId = selectedItem.Id,
                            Name = selectedItem.Name,
                            Price = selectedItem.Price,
                            ItemClassId = selectedItem.ItemClassId,
                            TaxId = selectedItem.TaxId,
                            Quantity = 1,
                            GroupingId = groupingIdForItem,
                            ParentItemId = null,
                            PackId = packIdForItem
                        });

                        pendingParents.Add(new PendingParent
                        {
                            ParentItem = selectedItem,
                            RecipeParent = recipeParent,
                            ItemParentId = itemParentId,
                            Quantity = recipeParent.Quantity,
                            OptionSetIndex = setIndex + 1,
                            TotalOptionSets = userRequestedQty,
                            GroupingId = groupingIdForItem,
                            CurrentOptionIndex = 1,
                            HasToppings = hasToppings,
                            ToppingClassId = selectedItem.ToppingClassId
                        });
                    }

                    cart.Items = existingItems.Concat(newItems).Concat(childrenToPreserve).ToList();
                    session.PendingParents = JsonConvert.SerializeObject(pendingParents);
                    session.CartData = _cartManager.SerializeCart(cart);

                    if (session.IsEditing)
                    {
                        session.IsEditing = false;
                        session.EditingGroupId = null;
                        session.CurrentState = "ORDER_CONFIRMATION";
                        await _uiManager.ShowOrderSummary(session, cart);
                    }
                    else
                    {
                        session.CurrentState = "ITEM_OPTIONS";
                        await _uiManager.ShowItemOptions(session.BusinessId, session.PhoneNumber, pendingParents.Last());
                    }
                    return;
                }
            }
            else if (hasToppings)
            {
                var toppings = await _orderService.GetToppingsAsync(selectedItem.ToppingClassId, session.RevenueCenterId);
                
                _cartManager.AddItemToCart(cart, new CartItem
                {
                    ItemId = selectedItem.Id,
                    Name = selectedItem.Name,
                    Price = selectedItem.Price,
                    ItemClassId = selectedItem.ItemClassId,
                    TaxId = selectedItem.TaxId,
                    Quantity = 1,
                    GroupingId = groupingIdForItem,
                    ParentItemId = null,
                    PackId = packIdForItem,
                    IsTopping = false
                });

                session.CartData = _cartManager.SerializeCart(cart);

                var pendingToppings = new PendingToppings
                {
                    MainItemId = selectedItem.Id,
                    MainItemName = selectedItem.Name,
                    GroupingId = groupingIdForItem,
                    Toppings = toppings ?? new List<ToppingItem>()
                };

                var pendingToppingsList = JsonConvert.DeserializeObject<List<PendingToppings>>(session.PendingToppingsQueue ?? "[]");
                pendingToppingsList.Add(pendingToppings);
                session.PendingToppingsQueue = JsonConvert.SerializeObject(pendingToppingsList);

                if (session.IsEditing)
                {
                    session.IsEditing = false;
                    session.EditingGroupId = null;
                }
                
                if (pendingToppingsList.Count == 1)
                {
                    session.CurrentState = "ITEM_TOPPINGS";
                    await _uiManager.ShowToppingsSelection(session.BusinessId, session.PhoneNumber, pendingToppings);
                }
                else
                {
                    await _uiManager.ShowPostAddOptions(session);
                    session.CurrentState = session.CurrentState == "ITEM_SELECTION_FROM_EDIT" ? "ITEM_SELECTION_FROM_EDIT" : "ITEM_SELECTION";
                }
                return;
            }
            else
            {
                int quantity = 1;
                if (session.IsEditing)
                {
                    var originalItem = cart.Items.FirstOrDefault(i =>
                        i.ItemId == session.EditingGroupId ||
                        i.GroupingId == session.EditingGroupId);

                    if (originalItem != null)
                        quantity = originalItem.Quantity;

                    if (childrenToPreserve.Any())
                    {
                        _cartManager.ClearChildren(childrenToPreserve);
                    }
                }

                _cartManager.AddItemToCart(cart, new CartItem
                {
                    ItemId = selectedItem.Id,
                    Name = selectedItem.Name,
                    Price = selectedItem.Price,
                    ItemClassId = selectedItem.ItemClassId,
                    TaxId = selectedItem.TaxId,
                    Quantity = quantity,
                    GroupingId = null,
                    ParentItemId = null,
                    PackId = packIdForItem
                });
            }

            if (session.IsEditing)
            {
                session.IsEditing = false;
                session.EditingGroupId = null;
            }

            session.CartData = _cartManager.SerializeCart(cart);
            
            var smallCatalog = session.CurrentMenuLevel == "products_small";
            if (smallCatalog)
            {
                await ProceedToCheckoutFlow(session);
            }
            else
            {
                await _uiManager.ShowPostAddOptions(session);
                session.CurrentState = session.CurrentState == "ITEM_SELECTION_FROM_EDIT" ? "ITEM_SELECTION_FROM_EDIT" : "ITEM_SELECTION";
            }
        }
        public async Task HandleItemOptions(OrderSession session, OrderCart cart, string message)
        {
            if (message == "CANCEL_OPTIONS")
            {
                session.CurrentState = "ITEM_SELECTION";
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Option selection canceled.\n\n" +
                    "Add another item or proceed to checkout." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));
                return;
            }

            var pendingParents = JsonConvert.DeserializeObject<List<PendingParent>>(session.PendingParents ?? "[]");
            if (!pendingParents.Any())
            {
                session.CurrentState = "ITEM_SELECTION";
                return;
            }

            var currentParent = pendingParents.First();
            var business = await _orderService.GetBusinessAsync(session.BusinessId);
            var optionIds = currentParent.RecipeParent.ItemParent.ItemsInParent.Select(o => o.ItemId).ToList();
            var catalogItems = await _orderService.GetItemsAsync(business.RestaurantId, session.RevenueCenterId, optionIds);

            if (message.StartsWith("OPT_PAGE_", StringComparison.OrdinalIgnoreCase))
            {
                var pageStr = message.Substring("OPT_PAGE_".Length);
                if (!int.TryParse(pageStr, out var optPage) || optPage < 1) optPage = 1;
                await _uiManager.ShowItemOptions(session.BusinessId, session.PhoneNumber, currentParent, optPage);
                return;
            }

            var optionIndex = currentParent.RecipeParent.ItemParent.ItemsInParent
                .FindIndex(o => o.ItemId == message);

            if (optionIndex == -1)
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Invalid option selected.\n\n" +
                    "Please choose from the list below." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));
                await _uiManager.ShowItemOptions(session.BusinessId, session.PhoneNumber, currentParent);
                return;
            }

            var option = currentParent.RecipeParent.ItemParent.ItemsInParent[optionIndex];
            currentParent.RecipeParent.ItemParent.ItemsInParent.RemoveAt(optionIndex);

            _cartManager.AddItemToCart(cart, new CartItem
            {
                ItemId = option.ItemId,
                Name = option.ItemName,
                Price = 0,
                ItemClassId = option.ItemClassId,
                ParentItemId = currentParent.RecipeParent.ItemParent.Id,
                GroupingId = currentParent.GroupingId,
                Quantity = 1
            });

            currentParent.CurrentOptionIndex++;
            session.CartData = _cartManager.SerializeCart(cart);

            if (currentParent.CurrentOptionIndex <= currentParent.RecipeParent.Quantity)
            {
                session.PendingParents = JsonConvert.SerializeObject(pendingParents);
                await _uiManager.ShowItemOptions(session.BusinessId, session.PhoneNumber, currentParent);
            }
            else
            {
                pendingParents.Remove(currentParent);
                session.PendingParents = JsonConvert.SerializeObject(pendingParents);

                if (currentParent.HasToppings && !string.IsNullOrEmpty(currentParent.ToppingClassId))
                {
                    var toppings = await _orderService.GetToppingsAsync(currentParent.ToppingClassId, session.RevenueCenterId);
                    var pendingToppings = new PendingToppings
                    {
                        MainItemId = currentParent.ParentItem.Id,
                        MainItemName = currentParent.ParentItem.Name,
                        GroupingId = currentParent.GroupingId,
                        Toppings = toppings ?? new List<ToppingItem>()
                    };

                    var pendingToppingsList = JsonConvert.DeserializeObject<List<PendingToppings>>(session.PendingToppingsQueue ?? "[]");
                    pendingToppingsList.Insert(0, pendingToppings);
                    session.PendingToppingsQueue = JsonConvert.SerializeObject(pendingToppingsList);
                }

                if (pendingParents.Any())
                {
                    await ProcessNextParentOptions(session, cart);
                }
                else if (JsonConvert.DeserializeObject<List<PendingToppings>>(session.PendingToppingsQueue ?? "[]").Any())
                {
                    var pendingToppingsList = JsonConvert.DeserializeObject<List<PendingToppings>>(session.PendingToppingsQueue ?? "[]");
                    session.CurrentState = "ITEM_TOPPINGS";
                    await _uiManager.ShowToppingsSelection(session.BusinessId, session.PhoneNumber, pendingToppingsList.First());
                }
                else
                {
                    var smallCatalog = session.CurrentMenuLevel == "products_small";
                    if (smallCatalog)
                    {
                        await ProceedToCheckoutFlow(session);
                    }
                    else
                    {
                        await _uiManager.ShowPostAddOptions(session);
                        session.CurrentState = session.CurrentState == "ITEM_SELECTION_FROM_EDIT" ? "ITEM_SELECTION_FROM_EDIT" : "ITEM_SELECTION";
                    }
                }
            }
        }

        public async Task HandleOrderConfirmation(OrderSession session, OrderCart cart, string message)
        {
            if (_validationService.IsDiscountRequest(message))
            {
                await HandleDiscountCodeRequest(session);
                return;
            }

            if (!_validationService.AllowsCheckout(session.CurrentState))
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "🔄 *Flow Incomplete*\n\n" +
                    "You need to complete the ordering process before reviewing your order.\n\n" +
                    "Please follow the steps below to continue." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));
                return;
            }

            if (!_validationService.HasCompletedRequiredSteps(session, "PLACE_ORDER"))
            {
                string missingInfo = "";
                if (string.IsNullOrEmpty(session.RevenueCenterId))
                    missingInfo = "location selection";
                else if (string.IsNullOrEmpty(session.DeliveryMethod))
                    missingInfo = "delivery method selection";
                else if (string.IsNullOrEmpty(session.DeliveryAddress) && session.DeliveryMethod == "Delivery")
                    missingInfo = "delivery address";

                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    $"📍 *Missing Information*\n\n" +
                    $"You need to complete: {missingInfo}\n\n" +
                    "Please follow the instructions below to continue.");
                return;
            }

            var business = await _orderService.GetBusinessAsync(session.BusinessId);
            var revenueCenters = await _orderService.GetRevenueCenters(session.BusinessId);
            var revenueCenter = revenueCenters.FirstOrDefault(rc => rc.Id == session.RevenueCenterId);
            
            if (message.Equals("CONFIRM_ORDER"))
            {
                await CreateOrder(session, cart);
                return;
            }
            else if (message.Equals("CANCEL_ORDER", StringComparison.OrdinalIgnoreCase))
            {
                await HandleCancelConfirmation(session);
            }
            else if (message.Equals("EDIT_ORDER", StringComparison.OrdinalIgnoreCase))
            {
                session.CurrentState = "EDIT_ORDER";
                await _uiManager.ShowEditOrderMenu(session, cart);
            }
            else
            {
                await _uiManager.ShowOrderSummary(session, cart);
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "Please select one of the options above:\n\n" +
                    "✅ Confirm Order - to proceed\n" +
                    "✏️ Edit Order - to modify\n" +
                    "❌ Cancel Order - to cancel");
            }
        }

        public async Task HandleEditOrder(OrderSession session, OrderCart cart, string message)
        {
            if (_validationService.IsDiscountRequest(message))
            {
                await HandleDiscountCodeRequest(session);
                return;
            }
            if (message == "BACK_TO_PACKS")
            {
                if (session.CurrentState == "PACK_SELECTION_ADD" || session.CurrentState.Contains("ADD"))
                {
                    session.CurrentState = "PACK_SELECTION_ADD";
                    await _uiManager.ShowPackSelectionMenu(session, cart, "ADD");
                }
                else
                {
                    session.CurrentState = "PACK_SELECTION_REMOVE";
                    await _uiManager.ShowPackSelectionMenu(session, cart, "REMOVE");
                }
                return;
            }
            var business = await _orderService.GetBusinessAsync(session.BusinessId);
            var revenueCenters = await _orderService.GetRevenueCenters(session.BusinessId);
            var revenueCenter = revenueCenters.FirstOrDefault(rc => rc.Id == session.RevenueCenterId);
            
            bool packagingEnabled = revenueCenter?.Packaging == true;

            if (packagingEnabled)
            {
                if (message.StartsWith("ADD_PACK_") || 
                    message.StartsWith("REMOVE_PACK_") ||
                    message.StartsWith("ADD_") && message.Contains("PACK") ||
                    message.StartsWith("REMOVE_") && message.Contains("PACK") ||
                    message == "ADD_NEW_PACK" ||
                    message.EndsWith("_NEW_PACK"))
                {
                    var action = message.StartsWith("ADD") ? "ADD" : "REMOVE";
                    await HandlePackSelection(session, cart, message, action);
                    return;
                }

                if (message.Equals("ADD_ITEM", StringComparison.OrdinalIgnoreCase))
                {
                    session.CurrentState = "PACK_SELECTION_ADD";
                    await _uiManager.ShowPackSelectionMenu(session, cart, "ADD");
                    return;
                }
                else if (message.Equals("REMOVE_ITEM", StringComparison.OrdinalIgnoreCase))
                {
                    session.CurrentState = "PACK_SELECTION_REMOVE";
                    await _uiManager.ShowPackSelectionMenu(session, cart, "REMOVE");
                    return;
                }
                else if (message == "BACK_TO_PACKS")
                {
                    if (session.CurrentState == "PACK_SELECTION_ADD" || session.CurrentState.Contains("ADD"))
                    {
                        await _uiManager.ShowPackSelectionMenu(session, cart, "ADD");
                    }
                    else
                    {
                        await _uiManager.ShowPackSelectionMenu(session, cart, "REMOVE");
                    }
                    return;
                }
            }
            else
            {
                if (message.Equals("ADD_ITEM", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleAddRequest(session);
                    return;
                }
                else if (message.Equals("REMOVE_ITEM", StringComparison.OrdinalIgnoreCase))
                {
                    session.CurrentState = "REMOVE_ITEM_PROMPT";
                    await _uiManager.ShowRemoveItemPrompt(session, cart);
                    return;
                }
            }

            if (message.Equals("BACK_TO_SUMMARY", StringComparison.OrdinalIgnoreCase))
            {
                if (!cart.Items.Any())
                {
                    session.CurrentState = "ITEM_SELECTION";
                    await _messagingService.SendTextMessageAsync(
                        session.BusinessId,
                        session.PhoneNumber,
                        "🛒 Your cart is empty.\n\n" +
                        "Please add some items before viewing the order summary.");
                    
                    await _uiManager.ShowMainMenu(session.BusinessId, session.PhoneNumber, "Click 'View items' to add items to your empty cart:");
                    return;
                }
                
                session.CurrentState = "ORDER_CONFIRMATION";
                await _uiManager.ShowOrderSummary(session, cart);
                return;
            }
            else if (message.Equals("REMOVE_ITEM", StringComparison.OrdinalIgnoreCase))
            {
                session.CurrentState = "REMOVE_ITEM_PROMPT";
                await _uiManager.ShowRemoveItemPrompt(session, cart);
                return;
            }
            else
            {
                await HandleEditItemByNumber(session, cart, message);
                return;
            }
        }

        public async Task HandleRemoveItemByNumber(OrderSession session, OrderCart cart, string message)
        {
            if (_validationService.IsDiscountRequest(message))
            {
                await HandleDiscountCodeRequest(session);
                return;
            }
            if (message == "BACK_TO_SUMMARY")
            {
                session.CurrentState = "ORDER_CONFIRMATION";
                await _uiManager.ShowOrderSummary(session, cart);
                return;
            }

            if (message == "BACK_TO_PACKS")
            {
                session.CurrentState = "PACK_SELECTION_REMOVE";
                await _uiManager.ShowPackSelectionMenu(session, cart, "REMOVE");
                return;
            }

            if (message.StartsWith("REMOVE_PACK_") || message.StartsWith("ADD_PACK_"))
            {
                await HandlePackSelection(session, cart, message,
                    message.StartsWith("REMOVE_PACK_") ? "REMOVE" : "ADD");
                return;
            }

            if (message == "0")
            {
                var revenueCenters = await _orderService.GetRevenueCenters(session.BusinessId);
                var revenueCenter = revenueCenters.FirstOrDefault(rc => rc.Id == session.RevenueCenterId);
                bool packagingEnabled = revenueCenter?.Packaging == true;

                if (packagingEnabled && !string.IsNullOrEmpty(session.CurrentPackId))
                {
                    var totalPacks = _cartManager.GetPacks(cart).Count;
                    if (totalPacks > 1)
                    {
                        _cartManager.RemovePack(cart, session.CurrentPackId);
                        session.CartData = _cartManager.SerializeCart(cart);

                        await _messagingService.SendTextMessageAsync(
                            session.BusinessId,
                            session.PhoneNumber,
                            $"✅ Removed {session.CurrentPackId.Replace("pack", "Pack ")} from your order.");

                        session.CurrentState = "PACK_SELECTION_REMOVE";
                        await _uiManager.ShowPackSelectionMenu(session, cart, "REMOVE");
                        return;
                    }
                    else
                    {
                        await _messagingService.SendTextMessageAsync(
                            session.BusinessId,
                            session.PhoneNumber,
                            "❌ Cannot remove the only pack. Add another pack first or cancel the order." +
                            MessageFormattingHelper.FormatHelpContactFooter(session));
                        await _uiManager.ShowEditPackMenu(session, cart, session.CurrentPackId);
                        return;
                    }
                }
            }

            if (int.TryParse(message, out int itemNumber))
            {
                var itemList = _cartManager.GetItemListForEditing(cart, session.CurrentPackId);

                if (!_validationService.IsValidItemNumber(message, itemList.Count))
                {
                    await _messagingService.SendTextMessageAsync(
                        session.BusinessId,
                        session.PhoneNumber,
                        "❌ Invalid item number. Please enter a valid number." +
                        MessageFormattingHelper.FormatHelpContactFooter(session));

                    if (!string.IsNullOrEmpty(session.CurrentPackId))
                    {
                        await _uiManager.ShowEditPackMenu(session, cart, session.CurrentPackId);
                    }
                    else
                    {
                        await _uiManager.ShowRemoveItemPrompt(session, cart);
                    }
                    return;
                }

                var itemToRemove = itemList[itemNumber - 1];

                _cartManager.RemoveItemByNumber(cart, itemNumber, session.CurrentPackId);

                session.CartData = _cartManager.SerializeCart(cart);

                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    $"✅ Removed {itemToRemove.Name} from cart.");

                var revenueCentersCheck = await _orderService.GetRevenueCenters(session.BusinessId);
                var revenueCenterCheck = revenueCentersCheck.FirstOrDefault(rc => rc.Id == session.RevenueCenterId);
                bool packagingEnabledCheck = revenueCenterCheck?.Packaging == true;

                if (packagingEnabledCheck && !string.IsNullOrEmpty(session.CurrentPackId))
                {
                    var remainingItems = _cartManager.GetItemsByPack(cart, session.CurrentPackId);
                    if (remainingItems.Any())
                    {
                        session.CurrentState = "REMOVE_ITEM_PROMPT";
                        await _uiManager.ShowEditPackMenu(session, cart, session.CurrentPackId);
                    }
                    else
                    {
                        session.CurrentState = "PACK_SELECTION_REMOVE";
                        await _uiManager.ShowPackSelectionMenu(session, cart, "REMOVE");
                    }
                }
                else
                {
                    await _uiManager.ShowEditOrderMenu(session, cart);
                }
            }
            else
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Please enter a valid item number or use the buttons below." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));

                if (!string.IsNullOrEmpty(session.CurrentPackId))
                {
                    await _uiManager.ShowEditPackMenu(session, cart, session.CurrentPackId);
                }
                else
                {
                    await _uiManager.ShowRemoveItemPrompt(session, cart);
                }
            }
        }

        public async Task HandleEditItemByNumber(OrderSession session, OrderCart cart, string message)
        {
            var itemList = _cartManager.GetItemListForEditing(cart, session.CurrentPackId);

            if (!_validationService.IsValidItemNumber(message, itemList.Count))
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Invalid item number. Please enter a valid number." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));
                await _uiManager.ShowEditOrderMenu(session, cart);
                return;
            }

            var itemNumber = int.Parse(message);
            var selectedItem = itemList[itemNumber - 1];

            await _messagingService.SendTextMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                $"✏️ Editing {selectedItem.Name} is not yet implemented.\n\nYou can remove it and add a new item instead.");

            await _uiManager.ShowEditOrderMenu(session, cart);
        }



        public async Task HandleCancelRequest(OrderSession session)
        {
            session.CurrentState = "CANCELLED";
            session.CartData = "{}";
            session.PendingParents = "[]";
            session.DeliveryMethod = null;
            session.DeliveryAddress = null;
            session.RevenueCenterId = null;
            session.Email = null;
            session.Notes = null;
            session.IsEditing = false;
            session.EditingGroupId = null;

            var cancelMessage = "❌ Order cancelled.\n\n" +
                            "Thank you for using our service. You can start a new order anytime." +
                            MessageFormattingHelper.FormatHelpContactFooter(session);

            await _messagingService.SendTextMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                cancelMessage);
        }

        public async Task HandleCancelConfirmation(OrderSession session)
        {
            var buttons = new List<WhatsAppButton>
            {
                new() { Text = "✅ Yes, Cancel", Payload = "CONFIRM_CANCEL" },
                new() { Text = "❌ No, Continue", Payload = "CONTINUE_ORDER" }
            };

            await _messagingService.SendInteractiveMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                "❓ Are you sure you want to cancel?\n\n" +
                "This action cannot be undone.",
                buttons);

            session.CurrentState = "CANCEL_CONFIRMATION";
            await _db.SaveChangesAsync();
        }

        public async Task HandleCancelConfirmationResponse(OrderSession session, OrderCart cart, string message)
        {
            switch (message)
            {
                case "CONFIRM_CANCEL":
                    await HandleCancelRequest(session);
                    return;
                case "CONTINUE_ORDER":
                    var sessionCart = _cartManager.DeserializeCart(session.CartData);
                    
                    if (sessionCart.Items.Any())
                    {
                        session.CurrentState = "ORDER_CONFIRMATION";
                        await _db.SaveChangesAsync();
                        await _uiManager.ShowOrderSummary(session, sessionCart);
                    }
                    else if (!string.IsNullOrEmpty(session.RevenueCenterId))
                    {
                        session.CurrentState = "ITEM_SELECTION";
                        await _db.SaveChangesAsync();
                        await _uiManager.ShowMainMenu(session.BusinessId, session.PhoneNumber, "Click 'View items' to add items to your cart:");
                    }
                    else
                    {
                        session.CurrentState = "LOCATION_SELECTION";
                        await _db.SaveChangesAsync();
                        await _uiManager.ResendWelcomeButtons(session.BusinessId, session.PhoneNumber);
                    }
                    break;
                default:
                    await _messagingService.SendTextMessageAsync(
                        session.BusinessId,
                        session.PhoneNumber,
                        "Please select one of the options below:\n\n" +
                        "✅ Yes, Cancel - to cancel order\n" +
                        "❌ No, Continue - to continue order");
                    break;
            }
        }

        public async Task HandleAddRequest(OrderSession session)
        {
            var cart = _cartManager.DeserializeCart(session.CartData);
            session.CartData = _cartManager.SerializeCart(cart);

            session.IsEditing = false;
            session.EditingGroupId = null;
            session.CurrentState = "ITEM_SELECTION_FROM_EDIT";
            
            await _uiManager.ShowMainMenu(session.BusinessId, session.PhoneNumber, 
                "Click 'View items' to add more items to your cart:");
        }

        public async Task HandleNotesCollection(OrderSession session, OrderCart cart, string message)
        {
            if (_validationService.IsDiscountRequest(message))
            {
                await HandleDiscountCodeRequest(session);
                return;
            }
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (message.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                session.Notes = null;
                // await _messagingService.SendTextMessageAsync(
                //     session.BusinessId,
                //     session.PhoneNumber,
                //     "✅ No special instructions added.");

                session.CurrentState = "ORDER_CONFIRMATION";
                await _uiManager.ShowOrderSummary(session, cart);
                return;
            }

            if (message.Equals("EDIT_ORDER", StringComparison.OrdinalIgnoreCase))
            {
                session.CurrentState = "EDIT_ORDER";
                await _uiManager.ShowEditOrderMenu(session, cart);
                return;
            }
            else if (message.Equals("CANCEL_ORDER", StringComparison.OrdinalIgnoreCase))
            {
                await HandleCancelConfirmation(session);
                return;
            }
            else if (message.Equals("BACK_TO_SUMMARY", StringComparison.OrdinalIgnoreCase))
            {
                session.CurrentState = "ORDER_CONFIRMATION";
                await _uiManager.ShowOrderSummary(session, cart);
                return;
            }

            if (message.Equals("PROFILE_BACK_TO_MENU", StringComparison.OrdinalIgnoreCase) ||
                message.Equals("⬅️ Back", StringComparison.OrdinalIgnoreCase))
            {
                if (session.DeliveryMethod == "Delivery" && !string.IsNullOrEmpty(session.DeliveryAddress))
                {
                    session.CurrentState = "ADDRESS_SAVE_PROMPT";
                    await _messagingService.SendTextMessageAsync(
                        session.BusinessId,
                        session.PhoneNumber,
                    "Save for next time?\n\nUse \"Manage Profile\" to edit anytime.");
                }
                else if (session.DeliveryMethod == "Delivery")
                {
                    session.CurrentState = "DELIVERY_ADDRESS";
                    await _messagingService.SendTextMessageAsync(
                        session.BusinessId,
                        session.PhoneNumber,
                        "📝 Please enter your delivery address:");
                }
                else
                {
                    session.CurrentState = "DELIVERY_METHOD";
                    await _uiManager.AskDeliveryMethod(session);
                }
                return;
            }
            
            var profileButtonPayloads = new[] { 
                "PROFILE_EMAIL", "PROFILE_ADDRESSES", "PROFILE_ADD_EMAIL", "PROFILE_REMOVE_EMAIL",
                "PROFILE_ADD_ADDRESS", "PROFILE_REMOVE_ADDRESS",
                "PROFILE_CONTINUE_ORDER", "PROFILE_BACK_TO_MAIN"
            };
            
            if (profileButtonPayloads.Contains(message))
            {
                return;
            }

            if (message.Trim().Length > 0 && !IsSystemMessage(message))
            {
                session.Notes = message.Trim();
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    $"✅ Notes added!");

                await ProceedToCheckoutFlow(session);
            }
            else
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Please enter valid special instructions or click 'none' to skip." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));
            }
        }

        private bool IsSystemMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return true;
                
            var trimmedMessage = message.Trim().ToLower();
            
            var systemPatterns = new[] { "hi", "hello", "hey", "start", "begin", "help", "menu" };
            var isSystemPattern = systemPatterns.Any(pattern => trimmedMessage.Equals(pattern));
            
            var isGreeting = trimmedMessage.Length <= 5 && systemPatterns.Any(pattern => trimmedMessage.Contains(pattern));
            
            return isSystemPattern || isGreeting;
        }

        public async Task HandleItemToppings(OrderSession session, OrderCart cart, string message)
        {
            var pendingToppingsList = JsonConvert.DeserializeObject<List<PendingToppings>>(session.PendingToppingsQueue ?? "[]");
            if (!pendingToppingsList.Any())
            {
                await ProceedAfterToppings(session, cart);
                return;
            }
            var currentPendingToppings = pendingToppingsList.First();

            if (message == "SKIP_TOPPINGS" || message == "NO_TOPPINGS" || message == "DONE_TOPPINGS")
            {
                pendingToppingsList.RemoveAt(0);
                session.PendingToppingsQueue = JsonConvert.SerializeObject(pendingToppingsList);

                if (pendingToppingsList.Any())
                {
                    session.CurrentState = "ITEM_TOPPINGS";
                    await _uiManager.ShowToppingsSelection(session.BusinessId, session.PhoneNumber, pendingToppingsList.First());
                }
                else
                {
                    await ProceedAfterToppings(session, cart);
                }
                return;
            }

            if (message == "BACK_TO_MENU")
            {
                if (currentPendingToppings != null && !string.IsNullOrEmpty(currentPendingToppings.MainItemId))
                {
                    cart.Items.RemoveAll(item => item.GroupingId == currentPendingToppings.GroupingId);
                    session.CartData = _cartManager.SerializeCart(cart);
                }
                
                pendingToppingsList.RemoveAt(0);
                session.PendingToppingsQueue = JsonConvert.SerializeObject(pendingToppingsList);
                
                session.CurrentState = "ITEM_SELECTION";
                await _uiManager.ShowMainMenu(session.BusinessId, session.PhoneNumber);
                return;
            }

            if (message.StartsWith("TOPPING_PAGE_"))
            {
                var pageStr = message.Substring("TOPPING_PAGE_".Length);
                if (!int.TryParse(pageStr, out var page) || page < 1) page = 1;
                await _uiManager.ShowToppingsSelection(session.BusinessId, session.PhoneNumber, currentPendingToppings, page);
                return;
            }

            var selectedTopping = currentPendingToppings.Toppings.FirstOrDefault(t => t.Id == message);
            if (selectedTopping != null)
            {
                _cartManager.AddItemToCart(cart, new CartItem
                {
                    ItemId = selectedTopping.Id,
                    Name = selectedTopping.Name,
                    Price = selectedTopping.Price,
                    ItemClassId = selectedTopping.ItemClassId,
                    TaxId = selectedTopping.TaxId,
                    Quantity = 1,
                    IsTopping = true,
                    MainItemId = currentPendingToppings.MainItemId,
                    GroupingId = currentPendingToppings.GroupingId,
                    PackId = session.CurrentPackId ?? "pack1"
                });

                if (currentPendingToppings.SelectedToppingIds == null)
                {
                    currentPendingToppings.SelectedToppingIds = new List<string>();
                }
                currentPendingToppings.SelectedToppingIds.Add(selectedTopping.Id);
                
                pendingToppingsList[0] = currentPendingToppings;
                session.PendingToppingsQueue = JsonConvert.SerializeObject(pendingToppingsList);

                session.CartData = _cartManager.SerializeCart(cart);
                
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    $"✅ Added {selectedTopping.Name} - ₦{selectedTopping.Price:N2}");

                await _uiManager.ShowToppingsSelection(session.BusinessId, session.PhoneNumber, currentPendingToppings);
            }
            else
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Invalid topping selection.\n\nPlease choose from the list below." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));
                
                await _uiManager.ShowToppingsSelection(session.BusinessId, session.PhoneNumber, currentPendingToppings);
            }
        }

        private async Task ProceedAfterToppings(OrderSession session, OrderCart cart)
        {
            var smallCatalog = session.CurrentMenuLevel == "products_small";
            if (smallCatalog)
            {
                await ProceedToCheckoutFlow(session);
            }
            else
            {
                session.CurrentState = session.CurrentState == "ITEM_SELECTION_FROM_EDIT" ? "ITEM_SELECTION_FROM_EDIT" : "ITEM_SELECTION";
                await _uiManager.ShowPostAddOptions(session);
            }
        }

        public async Task CreateOrder(OrderSession session, OrderCart cart)
        {
            var business = await _orderService.GetBusinessAsync(session.BusinessId);
            if (business == null)
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Order failed due to configuration error." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));
                return;
            }

            decimal subtotal = _cartManager.CalculateSubtotal(cart);
            var taxes = await _orderService.GetTaxesAsync(business.RestaurantId, business.BusinessToken);
            var tax = taxes.FirstOrDefault();
            string taxId = tax?.TaxId;
            decimal taxRate = tax?.Rate ?? 0;
            decimal taxAmount = 0;

            if (session.TaxExclusive && taxRate > 0)
            {
                taxAmount = Math.Round(subtotal * (taxRate / 100), 2, MidpointRounding.AwayFromZero);
            }

            var packs = _cartManager.GetPacks(cart);
            int packCount = packs.Count;
            
            var baseCharges = await _orderService.GetChargesAsync(business.RestaurantId, session.RevenueCenterId, "TakeOut");
            var confirmationTime = DateTime.UtcNow;
            var activeBaseCharges = baseCharges
                .Where(c => c.IsActive && c.ExpiryDate > confirmationTime)
                .ToList();
            
            var packCharges = new List<OrderChargeInfo>();
            var nonPackCharges = new List<OrderChargeInfo>();
            
            foreach (var charge in activeBaseCharges)
            {
                if (charge.Name != null &&
                    (charge.Name.ToLower().Contains("pack") ||
                    charge.Name.ToLower().Contains("disposable") ||
                    charge.Name.ToLower().Contains("plastic")))
                {
                    for (int i = 0; i < packCount; i++)
                    {
                        packCharges.Add(charge);
                    }
                }
                else
                {
                    nonPackCharges.Add(charge);
                }
            }


            var deliveryCharges = new List<OrderChargeInfo>();
            if (session.DeliveryMethod == "Delivery" && !string.IsNullOrEmpty(session.DeliveryChargeId))
            {
                var allDeliveryCharges = await _orderService.GetChargesAsync(business.RestaurantId, session.RevenueCenterId, "Delivery");
                var selectedDeliveryCharge = allDeliveryCharges.FirstOrDefault(c => c.Id == session.DeliveryChargeId);

                if (selectedDeliveryCharge != null && selectedDeliveryCharge.IsActive && selectedDeliveryCharge.ExpiryDate > confirmationTime)
                {
                    deliveryCharges.Add(selectedDeliveryCharge);
                }
            }

            var allCharges = nonPackCharges.Concat(packCharges).Concat(deliveryCharges).ToList();

            if (session.DiscountAmount > 0 && session.DiscountType == "Percent")
            {
                session.DiscountAmount = Math.Round(subtotal * (session.DiscountValue / 100), 2);
            }

            decimal chargesAmount = allCharges.Sum(c => c.Amount);

            decimal total = Math.Round(subtotal + taxAmount + chargesAmount - session.DiscountAmount, 2, MidpointRounding.AwayFromZero);

            var contactPhone = await _whatsAppProfileService.GetContactPhoneAsync(session.BusinessId, session.PhoneNumber);
            var phoneForOrder = string.IsNullOrEmpty(contactPhone) ? session.PhoneNumber : contactPhone;

            var orderRequest = new OrderRequest
            {
                RestaurantId = business.RestaurantId,
                RevenueCenterId = session.RevenueCenterId,
                Taxes = new List<TaxRequest>
                {
                    new() { TaxId = taxId, Amount = taxAmount, Rate = taxRate }
                },
                Items = cart.Items.Select(item => new OrderItemRequest
                {
                    GroupingId = item.GroupingId,
                    ItemId = item.ItemId,
                    ItemClassId = item.ItemClassId,
                    Quantity = item.Quantity,
                    Name = item.Name,
                    Price = item.Price,
                    Amount = string.IsNullOrEmpty(item.ParentItemId) ? item.Price * item.Quantity : 0
                }).ToList(),
                Charges = allCharges.Select(c => new OrderCharge
                {
                    OrderChargeId = c.Id,
                }).ToList(),
                SourceId = business.SourceId,
                PhoneNumber = session.PhoneNumber,
                CustomerName = session.CustomerName,
                Email = $"{session.PhoneNumber.Replace("+", "")}@gmail.com",
                ServiceType = session.DeliveryMethod,
                Address = session.DeliveryMethod == "Delivery" ? session.DeliveryAddress ?? "Address Not Provided" : "PICKUP",
                Adjustments = session.Notes + 
                            (session.DeliveryMethod == "Delivery" ? "\n\n" + phoneForOrder : string.Empty),
                DiscountCode = session.DiscountCode,
                DiscountAmount = session.DiscountAmount,
                PaymentChannels = new List<PaymentChannel>
                {
                    new() { AmountPaid = total, Channel = "ThirdParty" }
                },
                CustomChannels = new List<CustomChannel>
                {
                    new()
                    {
                        Name = "Sterling bank",
                        Description = "Channel For Sterling/Embedly",
                        CustomChannelId = business.EmbedlyAccountId,
                        Percentage = 1,
                        Amount = total
                    }
                }
            };
            var order = new Order
            {
                OrderId = Guid.NewGuid().ToString(),
                BusinessId = session.BusinessId,
                PhoneNumber = session.PhoneNumber,
                CustomerName = session.CustomerName,
                Subtotal = subtotal,
                Tax = taxAmount,
                Charge = chargesAmount,
                Total = total,
                Status = "PENDING_PAYMENT",
                RevenueCenterId = session.RevenueCenterId,
                Items = cart.Items.Select(item => new OrderItem
                {
                    ItemId = Guid.NewGuid().ToString(),
                    ProductId = item.ItemId,
                    ItemName = item.Name,
                    Quantity = item.Quantity,
                    Price = item.Price
                }).ToList()
            };

            _orderService.AddOrder(order);

            try
            {
                var accountDetails = await _orderService.CreateOrderAsync(orderRequest, business.BusinessToken);

                //var paymentId = ExtractPaymentIdFromCheckoutLink(accountNumber);
                
                _orderService.RemoveOrderSession(session);

                /*var templateResult = await _messagingService.SendOrderTemplateAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    $"₦{total:N2}",
                    accountNumber);*/

                var templateMessageResult = await _messagingService.SendOrderV2Template(session.BusinessId,session.PhoneNumber,total,accountDetails);


                if (templateMessageResult.Success) { return; }

                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    $"✅ *Order Received!*\n\n" +
                    $"💰 Total: ₦{total:N2}\n\n" +
                    $"Please make a transfer using the Account Details Below" +
                    $"🏦 Account Bank: {accountDetails.bankName}\n"+
                    $"🔗 Account Number: {accountDetails.paymentAccount}\n" +
                    $"Account Name: {accountDetails.accountName}\n\n" +
                    $"After payment, you'll be updated on your order status.\n" +
                    $"You can start a new order anytime.");
                
            }
            catch (Exception)
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Order failed.\n\n" +
                    "Please try again or contact support." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));
                await _sessionManager.DeleteSession(session.BusinessId, session.PhoneNumber);
            }
        }

        private async Task ProcessNextParentOptions(OrderSession session, OrderCart cart)
        {
            var pendingParents = JsonConvert.DeserializeObject<List<PendingParent>>(session.PendingParents);
            if (!pendingParents.Any())
            {
                return;
            }

            var currentParent = pendingParents.First();

            await _uiManager.ShowItemOptions(
                session.BusinessId,
                session.PhoneNumber,
                currentParent
            );
        }

        public async Task HandleDiscountCodeRequest(OrderSession session)
        {
            if (!string.IsNullOrEmpty(session.DiscountCode))
            {
                var cart = _cartManager.DeserializeCart(session.CartData);
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    $"❌ A discount code ({session.DiscountCode}) is already applied.\n\nOnly one discount code can be used per order." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));
                await _uiManager.ShowOrderSummary(session, cart);
                return;
            }
            
            session.CurrentState = "WAITING_FOR_DISCOUNT_CODE";
            await _messagingService.SendTextMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                "🎟️ Please enter your discount code:");
        }

        public async Task HandleDiscountCodeEntry(OrderSession session, OrderCart cart, string discountCode)
        {
            if (discountCode.Equals("CONFIRM_ORDER", StringComparison.OrdinalIgnoreCase))
            {
                session.CurrentState = "ORDER_CONFIRMATION"; 
                await CreateOrder(session, cart);
                return;
            }

            if (discountCode.Equals("EDIT_ORDER", StringComparison.OrdinalIgnoreCase))
            {
                session.CurrentState = "EDIT_ORDER";
                await _uiManager.ShowEditOrderMenu(session, cart);
                return;
            }

            if (discountCode.Equals("CANCEL_ORDER", StringComparison.OrdinalIgnoreCase))
            {
                await HandleCancelConfirmation(session);
                return;
            }

            if (discountCode.Equals("BACK_TO_SUMMARY", StringComparison.OrdinalIgnoreCase) || 
                discountCode.Equals("BACK_TO_MAIN", StringComparison.OrdinalIgnoreCase))
            {
                session.CurrentState = "ORDER_CONFIRMATION";
                await _uiManager.ShowOrderSummary(session, cart);
                return;
            }
            if (string.IsNullOrWhiteSpace(discountCode))
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ No code entered. Please enter a valid discount code." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));
                return;
            }
            
            if (!string.IsNullOrEmpty(session.DiscountCode))
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    $"❌ A discount code ({session.DiscountCode}) is already applied.\n\nOnly one discount code can be used per order." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));
                session.CurrentState = "ORDER_CONFIRMATION";
                await _uiManager.ShowOrderSummary(session, cart);
                return;
            }

            var business = await _orderService.GetBusinessAsync(session.BusinessId);
            var response = await _orderService.ValidateDiscountCodeAsync(discountCode, business.RestaurantId);

            if (response?.Data != null && response.Data.IsActive)
            {
                var subtotal = _cartManager.CalculateSubtotal(cart);
                
                session.DiscountCode = response.Data.Code;
                session.DiscountType = response.Data.DiscountUseType;

                if (session.DiscountType == "Percent")
                {
                    session.DiscountValue = response.Data.Value;
                    session.DiscountAmount = Math.Round(subtotal * (response.Data.Value / 100), 2);
                }
                else if (session.DiscountType == "Amount")
                {
                    session.DiscountValue = response.Data.Amount;
                    session.DiscountAmount = response.Data.Amount;
                }

                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    $"✅ Discount '{session.DiscountCode}' applied!");
            }
            else
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ The discount code you entered is invalid or has expired." +
                    MessageFormattingHelper.FormatHelpContactFooter(session));
            }

            session.CurrentState = "ORDER_CONFIRMATION";
            await _uiManager.ShowOrderSummary(session, cart);
        }

        public async Task HandlePackSelection(OrderSession session, OrderCart cart, string message, string action = "ADD")
        {
            if (message == "BACK_TO_SUMMARY")
            {
                session.CurrentState = "ORDER_CONFIRMATION";
                await _uiManager.ShowOrderSummary(session, cart);
                return;
            }

            if (message == "BACK_TO_PACKS")
            {
                var currentAction = action ?? "ADD";
                await _uiManager.ShowPackSelectionMenu(session, cart, currentAction);
                return;
            }

            if (message == "ADD_NEW_PACK" || message.EndsWith("_NEW_PACK"))
            {
                var newPackId = _cartManager.GetNextPackId(cart);
                session.CurrentPackId = newPackId;
                session.CurrentState = "ITEM_SELECTION_FROM_EDIT";
                await _db.SaveChangesAsync();

                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    $"📦 {newPackId.Replace("pack", "Pack ")} created, you can now add items.");

                await _uiManager.ShowMainMenu(session.BusinessId, session.PhoneNumber, 
                    "Click 'View items' to add items to your new pack:");
                return;
            }

            if (message.StartsWith("ADD_PACK_") || message.StartsWith("REMOVE_PACK_"))
            {
                var parts = message.Split('_');
                if (parts.Length >= 3)
                {
                    var packId = parts[2];
                    session.CurrentPackId = packId;
                    await _db.SaveChangesAsync();
                    
                    if (message.StartsWith("ADD_PACK_"))
                    {
                        session.CurrentState = "ITEM_SELECTION_FROM_EDIT";
                        
                        await _messagingService.SendTextMessageAsync(
                            session.BusinessId,
                            session.PhoneNumber,
                            $"📦 Pack {packId.Replace("pack", "")} selected successfully.\n\nYou can now add items.");
                        await _uiManager.ShowMainMenu(session.BusinessId, session.PhoneNumber,
                            "Click 'View items' to add items to this pack:");
                    }
                    else if (message.StartsWith("REMOVE_PACK_"))
                    {
                        session.CurrentState = "REMOVE_ITEM_PROMPT";
                        await _uiManager.ShowEditPackMenu(session, cart, packId);
                    }
                }
                return;
            }

            if (message.StartsWith("ADD_") || message.StartsWith("REMOVE_"))
            {
                var parts = message.Split('_');
                if (parts.Length >= 3 && parts[2] == "PACK")
                {
                    var packId = string.Join("_", parts.Skip(3));
                    session.CurrentPackId = packId;
                    await _db.SaveChangesAsync();
                    
                    if (message.StartsWith("ADD_"))
                    {
                        session.CurrentState = "ITEM_SELECTION_FROM_EDIT";
                        
                        await _messagingService.SendTextMessageAsync(
                            session.BusinessId,
                            session.PhoneNumber,
                            $"📦 Pack {packId.Replace("pack", "")} selected successfully.\n\nYou can now add items.");
                        
                        await _uiManager.ShowMainMenu(session.BusinessId, session.PhoneNumber, 
                            "Click 'View items' to add items to this pack:");
                    }
                    else if (message.StartsWith("REMOVE_"))
                    {
                        await _uiManager.ShowEditPackMenu(session, cart, packId);
                    }
                }
                return;
            }
        }

        private string ExtractPaymentIdFromCheckoutLink(string checkoutLink)
        {
            try
            {
                if (Uri.TryCreate(checkoutLink, UriKind.Absolute, out var uri))
                {
                    var segments = uri.Segments;
                    if (segments.Length > 0)
                    {
                        var lastSegment = segments[segments.Length - 1];
                        return lastSegment.TrimEnd('/');
                    }
                }

                if (checkoutLink.Contains("/pay/"))
                {
                    var parts = checkoutLink.Split(new[] { "/pay/" }, StringSplitOptions.None);
                    if (parts.Length > 1)
                    {
                        return parts[1].TrimEnd('/');
                    }
                }
                return "payment-id";
            }
            catch
            {
                return "payment-id";
            }
        }

        private async Task HandleInvalidItemSelection(OrderSession session)
        {
            await _messagingService.SendTextMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                "🔄 *Flow Interrupted*\n\n" +
                "You're trying to add items, but we need to get you back on track.\n\n" +
                "Please complete the current step first." +
                MessageFormattingHelper.FormatHelpContactFooter(session));
            
            await GuideUserToNextStep(session);
        }

        private async Task HandleIncompleteFlowForItemSelection(OrderSession session)
        {
            await _messagingService.SendTextMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                "📍 *Missing Information*\n\n" +
                "You need to complete some steps before adding items.\n\n" +
                "Please follow the instructions below." +
                MessageFormattingHelper.FormatHelpContactFooter(session));
            
            await GuideUserToNextStep(session);
        }

        private async Task GuideUserToNextStep(OrderSession session)
        {
            if (string.IsNullOrEmpty(session.RevenueCenterId))
            {
                session.CurrentState = "LOCATION_SELECTION";
                await _uiManager.ShowLocationSelection(session);
            }
            else if (string.IsNullOrEmpty(session.DeliveryMethod))
            {
                session.CurrentState = "DELIVERY_METHOD";
                await _uiManager.AskDeliveryMethod(session);
            }
            else if (string.IsNullOrEmpty(session.DeliveryAddress) && session.DeliveryMethod == "Delivery")
            {
                session.CurrentState = "DELIVERY_ADDRESS";
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "📝 Please enter your delivery address:");
            }
        }

        public async Task SendSpecialInstructionsPrompt(OrderSession session)
        {
            var buttons = new List<WhatsAppButton>
            {
                new() { Text = "🚫 None", Payload = "none" }
            };
            await _messagingService.SendInteractiveMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                MessageConstants.SpecialInstructionsPrompt,
                buttons);
        }
    }
}
