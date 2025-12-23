using FusionComms.DTOs.WhatsApp;
using FusionComms.Entities.WhatsApp;
using FusionComms.Services.WhatsApp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using FusionComms.Utilities;

namespace FusionComms.Services.WhatsApp.Restaurants
{
    public class OrderUIManager
    {
        private readonly WhatsAppMessagingService _messagingService;
        private readonly OrderService _orderService;
        private readonly OrderCartManager _cartManager;
        private readonly OrderSessionManager _sessionManager;
        private readonly ProductSearchService _searchService;

        public OrderUIManager(
            WhatsAppMessagingService messagingService,
            OrderService orderService,
            OrderCartManager cartManager,
            OrderSessionManager sessionManager,
            ProductSearchService searchService)
        {
            _messagingService = messagingService;
            _orderService = orderService;
            _cartManager = cartManager;
            _sessionManager = sessionManager;
            _searchService = searchService;
        }

        public async Task SendWelcomeMessage(string businessId, string phoneNumber, string customerName = null)
        {
            var business = await _orderService.GetBusinessAsync(businessId);
            var businessName = business?.BusinessName ?? "our restaurant";
            var botName = business?.BotName ?? "Dyfin";

            var buttons = new List<WhatsAppButton>
            {
                new() { Text = "Start Order", Payload = "START_ORDER" },
                new() { Text = "Get Help", Payload = "GET_HELP" }
            };

            var greeting = !string.IsNullOrEmpty(customerName) 
                ? $"👋 Hey {customerName}!\n\n"
                : "👋 Hey there!\n\n";

            await _messagingService.SendInteractiveMessageAsync(
                businessId,
                phoneNumber,
                greeting + $"🤖 Welcome to {businessName}! I'm {botName}\n\n" +
                "What can I help you with today?",
                buttons);
        }

        public async Task SendHelpMessage(string businessId, string phoneNumber, string customerName = null)
        {
            var business = await _orderService.GetBusinessAsync(businessId);
            
            var formattedPhoneNumber = OrderSessionManager.FormatWhatsAppPhoneNumber(phoneNumber);
            var session = await _orderService.GetOrderSessionAsync(businessId, formattedPhoneNumber);
            var helpEmail = session?.HelpEmail ?? "No Support Email ";
            var helpPhone = session?.HelpPhoneNumber ?? "No Support Phone";
            
            await _messagingService.SendTextMessageAsync(
                businessId,
                phoneNumber,
                $"🛟 We're here to help!\n\n" +
                "📋 To get started: Tap 'Start Order'\n" +
                "👤 To manage profile: Send 'manage profile'\n" +
                $"📞 To call support: {helpPhone}\n" +
                $"📧 To email support: {helpEmail}\n\n" +
                "Let's get you sorted!");
        }

        public async Task SendPersonalizedGreeting(string businessId, string phoneNumber, string customerName, string businessName)
        {
            var business = await _orderService.GetBusinessAsync(businessId);
            var botName = business?.BotName ?? "Dyfin";
            
            var personalizedGreeting = !string.IsNullOrEmpty(customerName) 
                ? $"👋 Hey {customerName}!\n\n"
                : "👋 Hey there!\n\n";

            await _messagingService.SendTextMessageAsync(
                businessId,
                phoneNumber,
                $"{personalizedGreeting}🤖 Welcome to {businessName}! I'm {botName}\n\n");
        }

        public async Task ResendWelcomeButtons(string businessId, string phoneNumber)
        {
            var buttons = new List<WhatsAppButton>
            {
                new() { Text = "Start Order", Payload = "START_ORDER" },
                new() { Text = "Get Help", Payload = "GET_HELP" }
            };

            await _messagingService.SendInteractiveMessageAsync(
                businessId,
                phoneNumber,
                "Please choose an option:",
                buttons);
        }

        public async Task ShowLocationSelection(OrderSession session, string customerName = null)
        {
            var revenueCenters = await _orderService.GetRevenueCenters(session.BusinessId);
            var business = await _orderService.GetBusinessAsync(session.BusinessId);
            var businessName = (business?.BusinessName?.Trim()) ?? "our restaurant";
            var botName = business?.BotName ?? "Dyfin";

            var greeting = !string.IsNullOrEmpty(customerName)
                ? $"👋 Hey, {customerName}! Welcome to {businessName}, I'm {botName}."
                : $"👋 Hey there! Welcome to {businessName}, I'm {botName}.";

            if (!revenueCenters.Any())
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    $"❌ No locations available at this time.");
                return;
            }

            if (revenueCenters.Count == 1)
            {
                var singleRevenueCenter = revenueCenters.First();
                await AutoSelectLocation(session, singleRevenueCenter, greeting);
                return;
            }

            var sections = new List<WhatsAppSection>
            {
                new() {
                    Title = "Select Location",
                    Rows = revenueCenters.Select(rc => 
                        CreateWhatsAppRow(rc.Id, rc.Name, $"{rc.Address}, {rc.State}")
                    ).ToList()
                }
            };

            var listBodyText = $"{greeting}\n\n📍 Please select restaurant location:";

            await _messagingService.SendInteractiveListAsync(
                session.BusinessId,
                session.PhoneNumber,
                listBodyText,
                "Choose Location",
                sections);
        }

        private async Task AutoSelectLocation(OrderSession session, RevenueCenter revenueCenter, string greetingMessage = null)
        {
            if (revenueCenter.Restaurant == null)
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Restaurant configuration missing.\n\nPlease try again later.");
                return;
            }

            var nowWat = TimeZoneHelper.GetWatNow();

            var startUtc = revenueCenter.Restaurant.StartTime;
            var endUtc = revenueCenter.Restaurant.EndTime;

            var startWat = TimeZoneHelper.ToWat(startUtc);
            var endWat = TimeZoneHelper.ToWat(endUtc);

            var startToday = new DateTime(nowWat.Year, nowWat.Month, nowWat.Day, startWat.Hour, startWat.Minute, startWat.Second);
            var endToday = new DateTime(nowWat.Year, nowWat.Month, nowWat.Day, endWat.Hour, endWat.Minute, endWat.Second);

            if (endToday <= startToday)
            {
                if (nowWat >= startToday)
                    endToday = endToday.AddDays(1);
                else
                    startToday = startToday.AddDays(-1);
            }

            var combinedMessage = new StringBuilder();

            if (!string.IsNullOrEmpty(greetingMessage))
            {
                combinedMessage.AppendLine(greetingMessage);
                combinedMessage.AppendLine();
            }

            if (nowWat < startToday || nowWat > endToday)
            {
                session.RevenueCenterId = revenueCenter.Id;
                
                combinedMessage.AppendLine($"⏰ *We're currently closed*");
                combinedMessage.AppendLine();
                combinedMessage.AppendLine($"📍 {revenueCenter.Name}");
                combinedMessage.AppendLine($"🕐 We'll process your order at *{startToday:hh:mm tt}*");
                combinedMessage.AppendLine();
                combinedMessage.AppendLine("Would you like to continue?");

                await _messagingService.SendInteractiveMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    combinedMessage.ToString(),
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
            session.TaxExclusive = revenueCenter.Restaurant.TaxExclusive;
            session.HelpEmail = revenueCenter.HelpEmail;
            session.HelpPhoneNumber = revenueCenter.HelpPhoneNumber;

            if (!revenueCenter.PickupAvailable)
            {
                session.DeliveryMethod = "Delivery";
            }

            session.CurrentState = "ITEM_SELECTION";
            
            combinedMessage.AppendLine($"📍 {revenueCenter.Name}");
            combinedMessage.AppendLine($"🏠 {revenueCenter.Address}, {revenueCenter.State}");
            combinedMessage.AppendLine($"🕐 Operating Hours: {startToday:hh:mm tt} - {endToday:hh:mm tt}");

            await _messagingService.SendTextMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                combinedMessage.ToString());

            await ShowMainMenu(session.BusinessId, session.PhoneNumber, "Click 'View items' to add items to your cart:");
        }

        public async Task ShowMainMenu(string businessId, string phoneNumber, string message = null)
        {
            try
            {
                var formattedPhoneNumber = OrderSessionManager.FormatWhatsAppPhoneNumber(phoneNumber);
                var session = await _orderService.GetOrderSessionAsync(businessId, formattedPhoneNumber);
                
                string revenueCenterId = session?.RevenueCenterId;
                
                var allSetIds = (await _orderService.GetProductSets(businessId))
                    .Select(s => s.SetId)
                    .ToList();

                var allProducts = await _orderService.GetProductsBySetIds(businessId, allSetIds, revenueCenterId);
                var totalProducts = allProducts.Count;
                var featuredCount = allProducts.Count(p => p.IsFeatured);

                if (totalProducts <= 30)
                {
                    var allowedSets = (await _orderService.GetProductSets(businessId))
                        .OrderBy(s => s.Name)
                        .Take(10)
                        .Select(s => s.SetId)
                        .ToList();

                    var fullResponse = await _messagingService.SendFullCatalogMessageAsync(
                    businessId,
                    phoneNumber,
                    headerText: "Our Menu",
                        bodyText: message ?? "Click 'View items' to add items to your cart:",
                        revenueCenterId: revenueCenterId,
                        allowedSetIds: allowedSets);

                    if (!fullResponse.Success)
                    {
                        await _messagingService.SendTextMessageAsync(
                            businessId,
                            phoneNumber,
                            string.Format(MessageConstants.CatalogFetchFailed, MessageFormattingHelper.FormatHelpContactFooter(session)));
                        await _sessionManager.DeleteSession(businessId, phoneNumber);
                    }
                    else
                    {
                        session.CurrentMenuLevel = "products_small";
                    }
                }
                else
                {
                    if (featuredCount < 5)
                    {
                        var top30 = allProducts
                            .Where(p => !string.IsNullOrEmpty(p.RetailerId))
                            .OrderBy(p => p.Name)
                            .Take(30)
                            .ToList();

                        var sampleResponse = await _messagingService.SendSearchResultsCatalogMessageAsync(
                            businessId,
                            phoneNumber,
                            headerText: "Our Menu",
                            bodyText: "Here are some items to get you started.\n\n*Looking for something else? Check the next message.*",
                            revenueCenterId: revenueCenterId,
                            searchResults: top30);

                        if (!sampleResponse.Success)
                        {
                            await _messagingService.SendTextMessageAsync(
                                businessId,
                                phoneNumber,
                                string.Format(MessageConstants.CatalogFetchFailed, MessageFormattingHelper.FormatHelpContactFooter(session)));
                            await _sessionManager.DeleteSession(businessId, phoneNumber);
                            return;
                        }

                        await ShowSearchActionButtons(businessId, phoneNumber, session);
                        session.CurrentMenuLevel = "products_sample";
                    }
                    else
                    {
                        var featuredResponse = await _messagingService.SendCatalogMessageAsync(
                            businessId,
                            phoneNumber,
                            headerText: "Featured Menu",
                            bodyText: "Here are some of our popular items.\n\n*Looking for something else? Check the next message.*",
                            revenueCenterId: revenueCenterId);

                        if (!featuredResponse.Success)
                        {
                            await _messagingService.SendTextMessageAsync(
                                businessId,
                                phoneNumber,
                                string.Format(MessageConstants.CatalogFetchFailed, MessageFormattingHelper.FormatHelpContactFooter(session)));
                            await _sessionManager.DeleteSession(businessId, phoneNumber);
                            return;
                        }

                        await ShowSearchActionButtons(businessId, phoneNumber, session);
                        session.CurrentMenuLevel = "categories";
                    }
                }
            }
            catch (Exception)
            {
                try
                {
                    var session = await _orderService.GetOrderSessionAsync(businessId, phoneNumber);
                    await _messagingService.SendTextMessageAsync(
                        businessId,
                        phoneNumber,
                        string.Format(MessageConstants.CatalogFetchFailed, session != null ? MessageFormattingHelper.FormatHelpContactFooter(session) : string.Empty));

                    await _sessionManager.DeleteSession(businessId, phoneNumber);
                }
                catch (Exception)
                {
                }
            }
        }

        public async Task ShowCategoriesList(OrderSession session, int page = 1)
        {
            var revenueCenterId = session?.RevenueCenterId;
            var groupingEntities = await _orderService.GetProductSetGroupings(session.BusinessId);
            List<WhatsAppSection> sections = new();
            const int pageSize = 8;
            
            List<WhatsAppRow> navigationRows = new();
            List<WhatsAppRow> contentRows = new();

            if (groupingEntities.Any())
            {
                var ordered = groupingEntities.OrderBy(g => g.GroupName).ToList();
                var total = ordered.Count;
                contentRows = ordered
                    .Skip((Math.Max(page, 1) - 1) * pageSize)
                    .Take(pageSize)
                    .Select(g => CreateWhatsAppRow(
                        $"CAT_{g.Id}",
                        g.GroupName,
                        "View menu for this category"
                    ))
                    .ToList();

                var hasPrev = page > 1;
                var hasNext = (page * pageSize) < total;
                if (hasPrev)
                {
                    navigationRows.Add(CreateWhatsAppRow($"CAT_PAGE_{page - 1}", "⬅️ Prev", "Go to previous page"));
                }
                if (hasNext)
                {
                    navigationRows.Add(CreateWhatsAppRow($"CAT_PAGE_{page + 1}", "Next ➡️", "See more categories"));
                }

                sections.Add(new WhatsAppSection { Title = "Browse Categories", Rows = contentRows });
            }
            else
            {
                var setsOrdered = (await _orderService.GetProductSets(session.BusinessId))
                    .OrderBy(s => s.Name)
                    .ToList();
                var total = setsOrdered.Count;
                var pagedSets = setsOrdered
                    .Skip((Math.Max(page, 1) - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();

                contentRows = pagedSets.Select(s => CreateWhatsAppRow(
                    $"CAT_SET_{s.SetId}",
                    s.Name,
                    "View items in this group"
                )).ToList();

                var hasPrev = page > 1;
                var hasNext = (page * pageSize) < total;
                if (hasPrev)
                {
                    navigationRows.Add(CreateWhatsAppRow($"CAT_PAGE_{page - 1}", "⬅️ Prev", "Go to previous page"));
                }
                if (hasNext)
                {
                    navigationRows.Add(CreateWhatsAppRow($"CAT_PAGE_{page + 1}", "Next ➡️", "See more categories"));
                }

                sections.Add(new WhatsAppSection { Title = "Browse Categories", Rows = contentRows });
            };

            if (navigationRows.Any())
            {
                sections.Add(new WhatsAppSection { Title = "Navigate", Rows = navigationRows });
            }

            var listResp = await _messagingService.SendInteractiveListAsync(
                session.BusinessId,
                session.PhoneNumber,
                bodyText: "Select a category to view it's menu.",
                buttonText: "Choose",
                sections: sections);

            if (listResp.Success)
            {
                var cart = _cartManager.DeserializeCart(session.CartData);
                var backOptions = new List<WhatsAppButton>
                {
                    new() { Text = "⬅️ Back", Payload = "BACK_TO_MAIN" },
                };

                if (cart.Items.Count > 0)
                {
                    backOptions.Add(new() { Text = "🛒 Checkout", Payload = "PROCEED_CHECKOUT" });
                }

                await _messagingService.SendInteractiveMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "Use the buttons below to navigate:",
                    backOptions);
            }

            session.CurrentMenuLevel = "categories";
        }

        public async Task ShowCategoryProducts(OrderSession session, string groupIdOrSetId, bool isGroupingId, int subPage = 1)
        {
            var revenueCenterId = session.RevenueCenterId;
            List<string> allowedSetIds;

            if (isGroupingId)
            {
                var grouping = await _orderService.GetProductSetGroupingById(session.BusinessId, groupIdOrSetId);
                allowedSetIds = _orderService.ParseGroupingSetIds(grouping);
                session.CurrentCategoryGroup = groupIdOrSetId;
            }
            else
            {
                allowedSetIds = new List<string> { groupIdOrSetId };
                session.CurrentCategoryGroup = $"SET:{groupIdOrSetId}";
            }

            allowedSetIds = allowedSetIds?.Take(10).ToList() ?? new List<string>();

            var productsInCategory = await _orderService.GetProductsBySetIds(session.BusinessId, allowedSetIds, revenueCenterId);
            var sortedProducts = productsInCategory.OrderBy(p => p.Name).ToList();
            var totalProductsInCategory = sortedProducts.Count;

            if (totalProductsInCategory > 30)
            {
                var subs = await _orderService.GetSubcategoriesForSets(session.BusinessId, allowedSetIds, revenueCenterId);
                if (subs.Any())
                {
                    const int pageSize = 8;
                    var totalSubs = subs.Count;
                    var pagedSubs = subs
                        .Skip((Math.Max(subPage, 1) - 1) * pageSize)
                        .Take(pageSize)
                        .ToList();

                    var rows = pagedSubs.Select(s => CreateWhatsAppRow(
                        $"SUBCAT_{s}",
                        s,
                        "View menu for this subcategory"
                    )).ToList();

                    var navigationRows = new List<WhatsAppRow>();
                    var hasPrev = subPage > 1;
                    var hasNext = (subPage * pageSize) < totalSubs;
                    if (hasPrev)
                    {
                        navigationRows.Add(CreateWhatsAppRow($"SUBCAT_PAGE_{subPage - 1}", "⬅️ Prev", "Go to previous page"));
                    }
                    if (hasNext)
                    {
                        navigationRows.Add(CreateWhatsAppRow($"SUBCAT_PAGE_{subPage + 1}", "Next ➡️", "See more subcategories"));
                    }

                    var sections = new List<WhatsAppSection>
                    {
                        new() { Title = "Browse Subcategories", Rows = rows },
                    };

                    if (navigationRows.Any())
                    {
                        sections.Add(new() { Title = "Navigate", Rows = navigationRows });
                    }

                    var listResp = await _messagingService.SendInteractiveListAsync(
                        session.BusinessId,
                        session.PhoneNumber,
                        bodyText: (isGroupingId ? (await _orderService.GetProductSetGroupingById(session.BusinessId, groupIdOrSetId))?.GroupName : allowedSetIds.FirstOrDefault()) + ": Select a subcategory",
                        buttonText: "Choose",
                        sections: sections);

                    if (listResp.Success)
                    {
                        var cart = _cartManager.DeserializeCart(session.CartData);
                        var backOptions = new List<WhatsAppButton>
                        {
                            new() { Text = "⬅️ Back", Payload = "BACK_CATEGORIES" },
                        };

                        if (cart.Items.Count > 0)
                        {
                            backOptions.Add(new() { Text = "🛒 Checkout", Payload = "PROCEED_CHECKOUT" });
                        }

                        await _messagingService.SendInteractiveMessageAsync(
                            session.BusinessId,
                            session.PhoneNumber,
                            "Use the buttons below to navigate:",
                            backOptions);
                    }

                    session.CurrentMenuLevel = "subcategories";
                    return;
                }
            }

            var header = isGroupingId 
                ? (await _orderService.GetProductSetGroupingById(session.BusinessId, groupIdOrSetId))?.GroupName ?? "Our Menu"
                : (await _orderService.GetProductSets(session.BusinessId)).FirstOrDefault(s => s.SetId == groupIdOrSetId)?.Name ?? "Our Menu";

            header += " Menu";
            var body = "Click 'View items' to add items to your cart:";

            var resp = await _messagingService.SendFullCatalogMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                headerText: header,
                bodyText: body,
                revenueCenterId: revenueCenterId,
                allowedSetIds: allowedSetIds);

            if (resp.Success)
            {
                var cart = _cartManager.DeserializeCart(session.CartData);
                session.CurrentMenuLevel = "products";
                
                var backOptions = new List<WhatsAppButton>
                {
                    new() { Text = "⬅️ Back", Payload = "BACK_CATEGORIES" },
                };

                if (cart.Items.Count > 0)
                {
                    backOptions.Add(new() { Text = "🛒 Checkout", Payload = "PROCEED_CHECKOUT" });
                }

                await _messagingService.SendInteractiveMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "Use the buttons below to navigate:",
                    backOptions);
            }
            else
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Could not load items for this category. Please try another.");
                await ShowCategoriesList(session);
            }
        }

        public async Task ShowItemOptions(string businessId, string phoneNumber, PendingParent pendingParent, int page = 1)
        {
            var options = pendingParent.RecipeParent.ItemParent.ItemsInParent;
            var required = pendingParent.RecipeParent.Quantity;
            string itemIndicator = pendingParent.TotalOptionSets > 1
                ? $" (Set {pendingParent.OptionSetIndex} of {pendingParent.TotalOptionSets})"
                : "";

            string[] ordinals = { "first", "second", "third", "fourth", "fifth" };
            string ordinal = pendingParent.CurrentOptionIndex <= ordinals.Length
                ? ordinals[pendingParent.CurrentOptionIndex - 1]
                : pendingParent.CurrentOptionIndex.ToString();

            string bodyText = $"{pendingParent.ParentItem.Name}{itemIndicator} Requires {required} option(s)\n\n" +
                            $"📝 Select your {ordinal} option:";

            const int pageSize = 8;
            var total = options.Count;
            var paged = options
                .Skip((Math.Max(page, 1) - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            var rows = paged.Select((option, index) => CreateWhatsAppRow(
                option.ItemId,
                $"Option {index + 1}",
                $"{option.ItemName} - Choose as your {ordinal} option"
            )).ToList();

            var navigationRows = new List<WhatsAppRow>();
            var hasPrev = page > 1;
            var hasNext = (page * pageSize) < total;
            if (hasPrev)
            {
                navigationRows.Add(CreateWhatsAppRow($"OPT_PAGE_{page - 1}", "⬅️ Prev", "Go to previous page"));
            }
            if (hasNext)
            {
                navigationRows.Add(CreateWhatsAppRow($"OPT_PAGE_{page + 1}", "Next ➡️", "See more options"));
            }

            var sections = new List<WhatsAppSection>
            {
                new()
                {
                    Title = "Select Options",
                    Rows = rows
                }
            };

            if (navigationRows.Any())
            {
                sections.Add(new WhatsAppSection { Title = "Navigate", Rows = navigationRows });
            }

            await _messagingService.SendInteractiveListAsync(
                businessId,
                phoneNumber,
                bodyText: bodyText,
                buttonText: "Choose Option",
                sections: sections
            );
        }

        public async Task ShowEditOrderMenu(OrderSession session, OrderCart cart)
        {
            session.EditingGroupId = null;
            session.IsEditing = false;

            if (!cart.Items.Any())
            {
                await _messagingService.SendInteractiveMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "🛒 Your cart is empty.\n\n" +
                    "Let's add some items!",
                    new List<WhatsAppButton>
                    {
                    new() { Text = "➕ Add Item", Payload = "ADD_ITEM" },
                    new() { Text = "↩️ Back", Payload = "BACK_TO_SUMMARY" }
                    });
                session.CurrentState = "EDIT_ORDER";
                return;
            }

            var revenueCenters = await _orderService.GetRevenueCenters(session.BusinessId);
            var revenueCenter = revenueCenters.FirstOrDefault(rc => rc.Id == session.RevenueCenterId);
            bool packagingEnabled = revenueCenter?.Packaging == true;

            var itemList = new StringBuilder("✏️ *Edit Your Order*\n\n");

            if (packagingEnabled)
            {
                var packs = _cartManager.GetPacks(cart);
                foreach (var packId in packs)
                {
                    var itemsInPack = _cartManager.GetItemsByPack(cart, packId);
                    var packSubtotal = itemsInPack.Sum(i => i.Price * i.Quantity);

                    itemList.AppendLine($"📦 *Pack {packId.Replace("pack", "")}* - ₦{packSubtotal:N2}");

                    var processedGroups = new HashSet<string>();
                    var comboGroups = itemsInPack
                        .Where(i => !string.IsNullOrEmpty(i.GroupingId))
                        .GroupBy(i => i.GroupingId);

                    foreach (var group in comboGroups)
                    {
                        if (processedGroups.Contains(group.Key)) continue;
                        processedGroups.Add(group.Key);

                        var parent = group.FirstOrDefault(i => string.IsNullOrEmpty(i.ParentItemId));
                        var children = group.Where(i => !string.IsNullOrEmpty(i.ParentItemId));

                        if (parent != null)
                        {
                            var description = $"{parent.Name}: {string.Join(", ", children.Select(c => c.Name))}";
                            itemList.AppendLine($"   ➡️ {description}");
                        }
                    }

                    var standaloneItems = itemsInPack
                        .Where(i => string.IsNullOrEmpty(i.GroupingId) && string.IsNullOrEmpty(i.ParentItemId))
                        .GroupBy(i => i.ItemId);

                    foreach (var group in standaloneItems)
                    {
                        var item = group.First();
                        var quantity = group.Sum(i => i.Quantity);
                        itemList.AppendLine($"   ➡️ {item.Name} x{quantity}");
                    }
                    itemList.AppendLine();
                }
            }
            else
            {
                var itemNumber = 1;
                var processedGroups = new HashSet<string>();

                var comboGroups = cart.Items
                    .Where(i => !string.IsNullOrEmpty(i.GroupingId))
                    .GroupBy(i => i.GroupingId);

                foreach (var group in comboGroups)
                {
                    if (processedGroups.Contains(group.Key)) continue;
                    processedGroups.Add(group.Key);

                    var parent = group.FirstOrDefault(i => string.IsNullOrEmpty(i.ParentItemId));
                    var children = group.Where(i => !string.IsNullOrEmpty(i.ParentItemId));

                    if (parent != null)
                    {
                        var description = $"{parent.Name}: {string.Join(", ", children.Select(c => c.Name))}";
                        itemList.AppendLine($"{itemNumber}. {description}");
                        itemNumber++;
                    }
                }

                var standaloneItems = cart.Items
                    .Where(i => string.IsNullOrEmpty(i.GroupingId) && string.IsNullOrEmpty(i.ParentItemId))
                    .GroupBy(i => i.ItemId);

                foreach (var group in standaloneItems)
                {
                    var item = group.First();
                    var quantity = group.Sum(i => i.Quantity);
                    itemList.AppendLine($"{itemNumber}. {item.Name} x{quantity}");
                    itemNumber++;
                }
            }

            itemList.AppendLine("\nSelect an option below:");

            List<WhatsAppButton> buttons;

            buttons = new List<WhatsAppButton>
            {
                new() { Text = "➕ Add Item", Payload = "ADD_ITEM" },
                new() { Text = "🗑️ Remove Item", Payload = "REMOVE_ITEM" },
                new() { Text = "🧾 Order Summary", Payload = "BACK_TO_SUMMARY" }
            };

            await _messagingService.SendInteractiveMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                itemList.ToString(),
                buttons);

            session.CurrentState = "EDIT_ORDER";
        }

        public async Task ShowRemoveItemPrompt(OrderSession session, OrderCart cart)
        {
            var itemListForEditing = _cartManager.GetItemListForEditing(cart);

            if (!itemListForEditing.Any())
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "🛒 Your cart is empty. There are no items to remove.");
                await ShowEditOrderMenu(session, cart);
                return;
            }

            var itemList = new StringBuilder("🗑️ *Remove Item*\n\n");
            var itemNumber = 1;
            foreach (var (GroupId, ItemId, Name, Quantity, PackId) in itemListForEditing)
            {
                itemList.AppendLine($"{itemNumber}. {Name} x{Quantity}");
                itemNumber++;
            }

            itemList.AppendLine($"\nEnter the item number to remove (1 to {itemListForEditing.Count}):");

            var buttons = new List<WhatsAppButton>
            {
                new() { Text = "🧾 Order Summary", Payload = "BACK_TO_SUMMARY" }
            };

            await _messagingService.SendInteractiveMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                itemList.ToString(),
                buttons);
        }
        
        public async Task ShowPackSelectionMenu(OrderSession session, OrderCart cart, string action = "ADD")
        {
            var packList = _cartManager.GetPackListForEditing(cart);
            var sections = new List<WhatsAppSection>();

            var packRows = new List<WhatsAppRow>();
            foreach (var (packId, packName, itemCount) in packList)
            {
                packRows.Add(CreateWhatsAppRow(
                    $"{action}_PACK_{packId}",
                    $"{packName} ({itemCount} items)",
                    $"Select {packName.ToLower()} to {action.ToLower()} items"
                ));
            }

            sections.Add(new WhatsAppSection
            {
                Title = "Select Pack",
                Rows = packRows
            });

            if (action == "ADD")
            {
                sections.Add(new WhatsAppSection
                {
                    Title = "Other Options",
                    Rows = new List<WhatsAppRow>
                    {
                        CreateWhatsAppRow(
                            $"{action}_NEW_PACK",
                            "➕ Add New Pack",
                            "Create a new pack and add items to it"
                        )
                    }
                });
            }
            var actionText = action == "ADD" ? "add items to" : "remove items from";

            await _messagingService.SendInteractiveListAsync(
                session.BusinessId,
                session.PhoneNumber,
                $"📦 *Pack Management*\n\nPlease select a pack to {actionText}:",
                "Choose Pack",
                sections);

            var navButtons = new List<WhatsAppButton>
            {
                new() { Text = "🧾 Order Summary", Payload = "BACK_TO_SUMMARY" }
            };

            await _messagingService.SendInteractiveMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                "Use the buttons below to navigate:",
                navButtons);
        }

        public async Task ShowEditPackMenu(OrderSession session, OrderCart cart, string packId)
        {
            var itemsInPack = _cartManager.GetItemsByPack(cart, packId);
            
            if (!itemsInPack.Any())
            {
                var sections = new List<WhatsAppSection>
                {
                    new() {
                        Title = "Empty Pack",
                        Rows = new List<WhatsAppRow>
                        {
                            CreateWhatsAppRow($"ADD_ITEM_{packId}", "➕ Add Item", "Add items to this pack"),
                            CreateWhatsAppRow($"REMOVE_PACK_{packId}", "🗑️ Remove Pack", "Delete this empty pack")
                        }
                    }
                };

                await _messagingService.SendInteractiveListAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    $"📦 Pack {packId.Replace("pack", "")} is empty.\n\nWhat would you like to do?",
                    "Choose Action",
                    sections);
                return;
            }

            var itemList = new StringBuilder($"✏️ *Editing Pack {packId.Replace("pack", "")}*\n\n");
            
            var itemListForEditing = _cartManager.GetItemListForEditing(cart, packId);
            
            var itemNumber = 1;
            foreach (var (GroupId, ItemId, Name, Quantity, PackId) in itemListForEditing)
            {
                itemList.AppendLine($"{itemNumber}. {Name} x{Quantity}");
                itemNumber++;
            }

            var totalPacks = _cartManager.GetPacks(cart).Count;
            
            if (totalPacks > 1)
            {
                itemList.AppendLine($"\n🗑️ Enter item number to remove\n(enter '0' to remove entire pack):");
            }
            else
            {
                itemList.AppendLine($"\n🗑️ Enter item number to remove:");
            }

            var buttons = new List<WhatsAppButton>
            {
                new() { Text = "⬅️ Back to Packs", Payload = "BACK_TO_PACKS" },
                new() { Text = "🧾 Order Summary", Payload = "BACK_TO_SUMMARY" }
            };

            await _messagingService.SendInteractiveMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                itemList.ToString(),
                buttons);
        }

        public async Task ShowOrderSummary(OrderSession session, OrderCart cart)
        {
            if (string.IsNullOrEmpty(session.RevenueCenterId))
            {
                return;
            }

            if (!cart.Items.Any())
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "🛒 Your cart is empty.\n\nPlease add some items before viewing the order summary.");
                    await ShowMainMenu(session.BusinessId, session.PhoneNumber, "Click 'View items' to add items to your empty cart:");
                return;
            }

            var business = await _orderService.GetBusinessAsync(session.BusinessId);
            decimal subtotal = _cartManager.CalculateSubtotal(cart);
            var taxes = await _orderService.GetTaxesAsync(business.RestaurantId, business.BusinessToken);
            var tax = taxes.FirstOrDefault();
            decimal taxRate = tax?.Rate ?? 0;
            decimal taxAmount = session.TaxExclusive ? Math.Round(subtotal * (taxRate / 100), 2) : 0;

            var baseCharges = await _orderService.GetChargesAsync(business.RestaurantId, session.RevenueCenterId, "TakeOut");
            var summaryTime = DateTime.UtcNow;
            var activeBaseCharges = baseCharges.Where(c => c.IsActive && c.ExpiryDate > summaryTime).ToList();

            var packs = _cartManager.GetPacks(cart);

            var packChargesSummary = new List<OrderChargeInfo>();
            var nonPackChargesSummary = new List<OrderChargeInfo>();

            foreach (var charge in activeBaseCharges)
            {
                if (charge.Name != null &&
                    (charge.Name.ToLower().Contains("pack") ||
                    charge.Name.ToLower().Contains("disposable") ||
                    charge.Name.ToLower().Contains("plastic")))
                {
                    for (int i = 0; i < packs.Count; i++)
                    {
                        packChargesSummary.Add(charge);
                    }
                }
                else
                {
                    nonPackChargesSummary.Add(charge);
                }
            }

            var deliveryCharges = new List<OrderChargeInfo>();
            if (session.DeliveryMethod == "Delivery" && !string.IsNullOrEmpty(session.DeliveryChargeId))
            {
                var allDeliveryCharges = await _orderService.GetChargesAsync(business.RestaurantId, session.RevenueCenterId, "Delivery");
                var selectedDeliveryCharge = allDeliveryCharges.FirstOrDefault(c => c.Id == session.DeliveryChargeId);

                if (selectedDeliveryCharge != null && selectedDeliveryCharge.IsActive && selectedDeliveryCharge.ExpiryDate > summaryTime)
                {
                    deliveryCharges.Add(selectedDeliveryCharge);
                }
            }

            var applicableCharges = nonPackChargesSummary.Concat(packChargesSummary).Concat(deliveryCharges).ToList();
            decimal chargesAmount = applicableCharges.Sum(c => c.Amount);

            if (session.DiscountAmount > 0 && session.DiscountType == "Percent")
            {
                session.DiscountAmount = Math.Round(subtotal * (session.DiscountValue / 100), 2);
            }

            decimal total = Math.Round(subtotal + taxAmount + chargesAmount - session.DiscountAmount, 2, MidpointRounding.AwayFromZero);

            var summary = new StringBuilder("🧾 *Your Order Summary*\n\n");

            var revenueCenters = await _orderService.GetRevenueCenters(session.BusinessId);
            var revenueCenter = revenueCenters.FirstOrDefault(rc => rc.Id == session.RevenueCenterId);
            bool packagingEnabled = revenueCenter?.Packaging == true;

            if (packagingEnabled)
            {
                var allItemsGrouped = new Dictionary<string, List<CartItem>>();
                var showPackSubtotals = packs.Count > 1;

                foreach (var packId in packs)
                {
                    var itemsInPack = _cartManager.GetItemsByPack(cart, packId);
                    var packSubtotal = itemsInPack.Sum(i => i.Price * i.Quantity);

                    if (showPackSubtotals)
                    {
                        summary.AppendLine($"📦 *Pack {packId.Replace("pack", "")}*(₦{packSubtotal:N2})");
                    }
                    else
                    {
                        summary.AppendLine($"📦 *Pack {packId.Replace("pack", "")}*");
                    }

                    var processedGroups = new HashSet<string>();
                    var comboGroups = itemsInPack
                        .Where(i => !string.IsNullOrEmpty(i.GroupingId))
                        .OrderBy(i => i.GroupingId)
                        .ThenBy(i => i.ItemId)
                        .GroupBy(i => i.GroupingId);

                    foreach (var group in comboGroups)
                    {
                        if (processedGroups.Contains(group.Key)) continue;
                        processedGroups.Add(group.Key);

                        var parent = group.FirstOrDefault(i => string.IsNullOrEmpty(i.ParentItemId));
                        var children = group.Where(i => !string.IsNullOrEmpty(i.ParentItemId)).OrderBy(i => i.ItemId);
                        var toppings = group.Where(i => i.IsTopping).OrderBy(i => i.ItemId);                        
                        if (parent != null)
                        {
                            decimal itemPrice = parent.Price;
                            decimal totalGroupPrice = Math.Round(itemPrice * parent.Quantity, 2);
                            summary.AppendLine($"   ➡️ *{parent.Name}* x{parent.Quantity} - ₦{totalGroupPrice:N2}");
                            
                            foreach (var child in children.Where(c => !c.IsTopping))
                            {
                                summary.AppendLine($"      🔹 {child.Name} (included)");
                            }
                            
                            foreach (var topping in toppings)
                            {
                                decimal toppingPrice = Math.Round(topping.Price * topping.Quantity, 2);
                                summary.AppendLine($"      🔸 {topping.Name} - ₦{toppingPrice:N2}");
                            }
                        }
                    }

                    var standaloneItems = itemsInPack
                        .Where(i => string.IsNullOrEmpty(i.GroupingId) && string.IsNullOrEmpty(i.ParentItemId))
                        .GroupBy(i => i.ItemId);

                    foreach (var itemGroup in standaloneItems)
                    {
                        var firstItem = itemGroup.First();
                        int qty = itemGroup.Sum(i => i.Quantity);
                        decimal totalPrice = Math.Round(firstItem.Price * qty, 2);
                        summary.AppendLine($"   ➡️ *{firstItem.Name}* x{qty} - ₦{totalPrice:N2}");
                    }

                    summary.AppendLine();
                }

                if (summary.Length > 4)
                {
                    summary.Length -= 2;
                }

                summary.AppendLine();
            }
            else
            {
                var processedGroups = new HashSet<string>();
                var comboGroups = cart.Items
                    .Where(i => !string.IsNullOrEmpty(i.GroupingId))
                    .GroupBy(i => i.GroupingId);

                foreach (var group in comboGroups)
                {
                    if (processedGroups.Contains(group.Key)) continue;
                    processedGroups.Add(group.Key);

                    var parent = group.FirstOrDefault(i => string.IsNullOrEmpty(i.ParentItemId));
                    var children = group.Where(i => !string.IsNullOrEmpty(i.ParentItemId));
                    var toppings = group.Where(i => i.IsTopping);

                    if (parent != null)
                    {
                        decimal itemPrice = parent.Price;
                        decimal totalGroupPrice = Math.Round(itemPrice * parent.Quantity, 2);
                        summary.AppendLine($"➡️ *{parent.Name}* x{parent.Quantity} - ₦{totalGroupPrice:N2}");
                        
                        foreach (var child in children.Where(c => !c.IsTopping))
                        {
                            summary.AppendLine($"   🔹 {child.Name} (included)");
                        }
                        
                        foreach (var topping in toppings)
                        {
                            decimal toppingPrice = Math.Round(topping.Price * topping.Quantity, 2);
                            summary.AppendLine($"   🔸 {topping.Name} - ₦{toppingPrice:N2}");
                        }
                    }
                }

                var standaloneItems = cart.Items
                    .Where(i => string.IsNullOrEmpty(i.GroupingId) && string.IsNullOrEmpty(i.ParentItemId))
                    .GroupBy(i => i.ItemId);

                foreach (var itemGroup in standaloneItems)
                {
                    var firstItem = itemGroup.First();
                    int qty = itemGroup.Sum(i => i.Quantity);
                    decimal totalPrice = Math.Round(firstItem.Price * qty, 2);
                    summary.AppendLine($"➡️ *{firstItem.Name}* x{qty} - ₦{totalPrice:N2}");
                }

                summary.AppendLine();
            }

            summary.AppendLine($"💵 *Subtotal*: ₦{subtotal:N2}");
            summary.AppendLine($"📦 *Tax ({taxRate:N1}%)*: ₦{taxAmount:N2}");

            if (applicableCharges.Any())
            {
                summary.AppendLine($"⚡ *Charges*: ₦{chargesAmount:N2}");

                var groupedCharges = new Dictionary<string, (decimal TotalAmount, int Count)>();

                foreach (var charge in applicableCharges)
                {
                    var chargeKey = charge.Name ?? "Charge";
                    if (groupedCharges.ContainsKey(chargeKey))
                    {
                        var current = groupedCharges[chargeKey];
                        groupedCharges[chargeKey] = (current.TotalAmount + charge.Amount, current.Count + 1);
                    }
                    else
                    {
                        groupedCharges[chargeKey] = (charge.Amount, 1);
                    }
                }

                foreach (var (chargeName, (totalAmount, count)) in groupedCharges)
                {
                    if (count > 1)
                    {
                        summary.AppendLine($"   🧾{chargeName}: ₦{totalAmount:N2} (x{count})");
                    }
                    else
                    {
                        summary.AppendLine($"   🧾{chargeName}: ₦{totalAmount:N2}");
                    }
                }
            }

            if (session.DiscountAmount > 0)
            {
                string discountLabel = $"🎟️ *Discount ({session.DiscountCode})*";
                string discountValue = session.DiscountType == "Percent"
                    ? $"{session.DiscountValue}% off"
                    : $"-₦{session.DiscountAmount:N2}";

                summary.AppendLine($"{discountLabel}: {discountValue}");
            }
            
            summary.AppendLine($"💰 *Total*: ₦{total:N2}");

            if (!string.IsNullOrEmpty(session.Notes))
            {
                summary.AppendLine($"\n📝 *Special Instructions*:\n{session.Notes}");
            }

            var buttons = new List<WhatsAppButton>
            {
                new() { Text = "✅ Confirm Order", Payload = "CONFIRM_ORDER" },
                new() { Text = "✏️ Edit Order", Payload = "EDIT_ORDER" }
            };

            if (string.IsNullOrEmpty(session.DiscountCode))
            {
                buttons.Add(new() { Text = "🎟️ Apply Discount", Payload = "APPLY_DISCOUNT" });
            }
            else
            {
                buttons.Add(new() { Text = "❌ Cancel Order", Payload = "CANCEL_ORDER" });
            }

            await _messagingService.SendInteractiveMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                summary.ToString(),
                buttons,
                footer: "Powered by Foodease, a product of Fusion Intelligence");
        }

        public async Task AskDeliveryMethod(OrderSession session)
        {
            var revenueCenters = await _orderService.GetRevenueCenters(session.BusinessId);
            var revenueCenter = revenueCenters.FirstOrDefault(rc => rc.Id == session.RevenueCenterId);

            var buttons = new List<WhatsAppButton>
            {
                new() { Text = "🚚 Delivery", Payload = "DELIVERY" },
                new() { Text = "🏃 Pickup", Payload = "PICKUP" }
            };

            var message = "🛒 Great! You have items in your cart.\n\n" +
                        "How would you like to receive your order?";
            
            await _messagingService.SendInteractiveMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                message,
                buttons);
        }

        public async Task ShowPostAddOptions(OrderSession session)
        {
            var buttons = new List<WhatsAppButton>
            {
                new() { Text = "➕ Add More", Payload = "ADD_MORE" },
                new() { Text = "🛒 Checkout", Payload = "PROCEED_CHECKOUT" }
            };

            await _messagingService.SendInteractiveMessageAsync(
                session.BusinessId,
                session.PhoneNumber,
                "What would you like to do next?",
                buttons);
        }

        public async Task ShowDeliveryLocationSelection(OrderSession session, bool skipHoursCheck = false)
        {
            var business = await _orderService.GetBusinessAsync(session.BusinessId);
            if (business == null)
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Configuration error.\n\nPlease try again.");
                return;
            }

            var revenueCenters = await _orderService.GetRevenueCenters(session.BusinessId);
            var revenueCenter = revenueCenters.FirstOrDefault(rc => rc.Id == session.RevenueCenterId);
            
            if (revenueCenter?.Restaurant == null)
            {
                await _messagingService.SendTextMessageAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    "❌ Restaurant configuration missing.\n\nPlease try again.");
                return;
            }

            bool pickupAvailable = revenueCenter.PickupAvailable;
            
            if (!skipHoursCheck)
            {
                var nowWat = TimeZoneHelper.GetWatNow();
                var startWat = TimeZoneHelper.ToWat(revenueCenter.Restaurant.StartTime);
                var endWat = TimeZoneHelper.ToWat(revenueCenter.Restaurant.EndTime);

                var startToday = nowWat.Date.Add(startWat.TimeOfDay);
                var endToday = nowWat.Date.Add(endWat.TimeOfDay);

                if (endToday <= startToday)
                {
                    if (nowWat.TimeOfDay >= startWat.TimeOfDay)
                        endToday = endToday.AddDays(1);
                    else
                        startToday = startToday.AddDays(-1);
                }

                if (nowWat < startToday || nowWat > endToday)
                {
                    var nextOpen = nowWat > endToday ? startToday.AddDays(1) : startToday;

                    var buttons = new List<WhatsAppButton>
                    {
                        new() { Text = "✅ Proceed anyway", Payload = "PROCEED_DELIVERY" }
                    };
                    
                    if (pickupAvailable)
                    {
                        buttons.Add(new() { Text = "🔄 Switch to Pickup", Payload = "SWITCH_TO_PICKUP" });
                    }
                    buttons.Add(new() { Text = "❌ Cancel Order", Payload = "CANCEL_ORDER" });

                    await _messagingService.SendInteractiveMessageAsync(
                        session.BusinessId,
                        session.PhoneNumber,
                        "🚚 *Delivery currently unavailable*\n\n" +
                        $"🕐 Wiil be Avaialable at: *{nextOpen:hh:mm tt}*\n\n" +
                        "What would you like to do?",
                        buttons);
                    
                    session.CurrentState = "CONFIRM_CLOSED_DELIVERY";
                    await _sessionManager.UpdateSession(session);
                    return;
                }
            }

            var deliveryCharges = await _orderService.GetChargesAsync(business.RestaurantId, session.RevenueCenterId, "Delivery");

            if (!deliveryCharges.Any())
            {
                if (pickupAvailable)
                {
                    var buttons = new List<WhatsAppButton>
                    {
                        new() { Text = "✅ Yes", Payload = "SWITCH_TO_PICKUP_YES" },
                        new() { Text = "❌ No", Payload = "SWITCH_TO_PICKUP_NO" }
                    };

                    await _messagingService.SendInteractiveMessageAsync(
                        session.BusinessId,
                        session.PhoneNumber,
                        "🚚 Delivery service not available.\n\nWould you like to switch to pickup instead?",
                        buttons);

                    session.CurrentState = "DELIVERY_SWITCH_CONFIRMATION";
                    return;
                }
                else
                {
                    await _messagingService.SendTextMessageAsync(
                        session.BusinessId,
                        session.PhoneNumber,
                        "🚚 Delivery is not available for this location, and pickup is also not supported.\n\nPlease select a different location.");
                    
                    session.CurrentState = "LOCATION_SELECTION";
                    await ShowLocationSelection(session);
                    return;
                }
            }

            var deliveryTime = DateTime.UtcNow;
            var activeDeliveryCharges = deliveryCharges
                .Where(c => c.IsActive && c.ExpiryDate > deliveryTime)
                .Take(9)
                .ToList();

            var sections = new List<WhatsAppSection>
            {
                new()
                {
                    Title = "Select Delivery Area",
                    Rows = activeDeliveryCharges
                        .Select((charge, index) => CreateWhatsAppRow(
                            charge.Id,
                            $"Area {index + 1}",
                            charge.Name
                        )).ToList()
                }
            };

            if (pickupAvailable)
            {
                sections.Add(new()
                {
                    Title = "Other Options",
                    Rows = new List<WhatsAppRow>
                    {
                        CreateWhatsAppRow("LOCATION_NOT_LISTED", "Location not listed", "Switch to pickup")
                    }
                });
            }

            // Check if delivery flow is configured
            if (!string.IsNullOrEmpty(business.DeliveryFlowJson))
            {
                // Send the WhatsApp Flow instead of interactive list
                var flowResponse = await _messagingService.SendDeliveryFlowAsync(
                    session.BusinessId,
                    session.PhoneNumber,
                    business.DeliveryFlowJson);

                if (flowResponse.Success)
                {
                    session.CurrentState = "FLOW_IN_PROGRESS";
                    await _sessionManager.UpdateSession(session);
                    return;
                }
                // If flow fails, fall back to interactive list
            }

            await _messagingService.SendInteractiveListAsync(
                session.BusinessId,
                session.PhoneNumber,
                "📍 Please select your delivery area:",
                "Choose Area",
                sections);
        }

        public async Task ShowSearchActionButtons(string businessId, string phoneNumber, OrderSession session = null)
        {
            var buttons = new List<WhatsAppButton>
            {
                new() { Text = "🔍 Search Menu", Payload = "SEARCH" },
                new() { Text = "📖 Browse Menu", Payload = "FULL_MENU" }
            };

            if (session != null)
            {
                var cart = _cartManager.DeserializeCart(session.CartData);
                if (cart.Items.Count > 0)
                {
                    buttons.Add(new() { Text = "🛒 Checkout", Payload = "PROCEED_CHECKOUT" });
                }
            }

            await _messagingService.SendInteractiveMessageAsync(
                businessId,
                phoneNumber,
                "What would you like to do?",
                buttons);
        }

        public async Task ShowToppingsSelection(string businessId, string phoneNumber, PendingToppings pendingToppings, int page = 1)
        {
            var toppings = pendingToppings.Toppings;
            
            if (!toppings.Any())
            {
                var noToppingsMessage = $"*Add Toppings for {pendingToppings.MainItemName}*\n\n" +
                    "No toppings available for this item.\n\n" +
                    "You can continue without toppings or send 'help' if you expected toppings to be available.";

                var noToppingsButtons = new List<WhatsAppButton>
                {
                    new() { Text = "✅ Continue", Payload = "DONE_TOPPINGS" },
                };

                await _messagingService.SendInteractiveMessageAsync(
                    businessId,
                    phoneNumber,
                    noToppingsMessage,
                    noToppingsButtons);
                return;
            }

            const int pageSize = 8;
            var total = toppings.Count;
            var pagedToppings = toppings
                .Skip((Math.Max(page, 1) - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            var rows = pagedToppings.Select(topping => CreateWhatsAppRow(
                topping.Id,
                $"₦{topping.Price:N2}",
                $"Add {topping.Name} as topping"
            )).ToList();

            var navigationRows = new List<WhatsAppRow>();
            var hasPrev = page > 1;
            var hasNext = (page * pageSize) < total;

            if (hasPrev)
            {
                navigationRows.Add(CreateWhatsAppRow($"TOPPING_PAGE_{page - 1}", "⬅️ Prev", "Go to previous page"));
            }

            if (hasNext)
            {
                navigationRows.Add(CreateWhatsAppRow($"TOPPING_PAGE_{page + 1}", "Next ➡️", "See more toppings"));
            }

            var sections = new List<WhatsAppSection>
            {
                new()
                {
                    Title = "Select Toppings",
                    Rows = rows
                }
            };

            if (navigationRows.Any())
            {
                sections.Add(new WhatsAppSection { Title = "Navigate", Rows = navigationRows });
            }

            var bodyText = $"➕ *Add Toppings for {pendingToppings.MainItemName}*\n\n" +
                        "Please choose a topping (optional, extra charges apply)";

            await _messagingService.SendInteractiveListAsync(
                businessId,
                phoneNumber,
                bodyText,
                "Choose Topping",
                sections);

            var buttons = new List<WhatsAppButton>();
            if (pendingToppings.SelectedToppingIds?.Any() == true)
            {
                buttons.Add(new() { Text = "✅ Done", Payload = "DONE_TOPPINGS" });
            }
            else
            {
                buttons.Add(new() { Text = "⏭️ Skip", Payload = "SKIP_TOPPINGS" });
            }

            await _messagingService.SendInteractiveMessageAsync(
                businessId,
                phoneNumber,
                "All set? Choose your next step.",
                buttons);
        }

        public async Task ShowSearchPrompt(string businessId, string phoneNumber)
        {
            await _messagingService.SendTextMessageAsync(
                businessId,
                phoneNumber,
                ProductSearchService.SearchMessages.SEARCH_PROMPT);
        }

        public async Task ShowSearchResults(string businessId, string phoneNumber, List<WhatsAppProduct> results, string searchQuery)
        {
            if (!results.Any())
            {
                await _messagingService.SendTextMessageAsync(
                    businessId,
                    phoneNumber,
                    string.Format(ProductSearchService.SearchMessages.NO_SEARCH_RESULTS, searchQuery));
                
                await ShowSearchActionButtons(businessId, phoneNumber);
                return;
            }

            var revenueCenterId = (await _orderService.GetOrderSessionAsync(businessId, phoneNumber))?.RevenueCenterId;
            var allowedSetIds = results.Select(p => p.SetId).Distinct().Take(15).ToList();

            if (allowedSetIds.Any())
            {
                var bodyText = $"Here's what we found matching: *{searchQuery}*";
                
                var catalogResponse = await _messagingService.SendSearchResultsCatalogMessageAsync(
                    businessId,
                    phoneNumber,
                    headerText: $"🔍 Search Results",
                    bodyText: bodyText,
                    revenueCenterId: revenueCenterId,
                    searchResults: results);
                
            }

            var buttons = new List<WhatsAppButton>
            {
                new() { Text = "🔍 Search Menu", Payload = "SEARCH" },
                new() { Text = "📖 Browse Menu", Payload = "FULL_MENU" }
            };

            await _messagingService.SendInteractiveMessageAsync(
                businessId,
                phoneNumber,
                "Please select an option:",
                buttons);
        }

        private static WhatsAppRow CreateWhatsAppRow(string id, string title, string description)
        {
            var truncatedTitle = title.Length > 24 ? title.Substring(0, 21) + "..." : title;
            var truncatedDescription = description.Length > 72 ? description.Substring(0, 69) + "..." : description;
            
            return new WhatsAppRow
            {
                Id = id,
                Title = truncatedTitle,
                Description = truncatedDescription
            };
        }
    }
}
