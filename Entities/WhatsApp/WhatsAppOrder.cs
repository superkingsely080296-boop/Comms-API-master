using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FusionComms.Entities.WhatsApp
{
    [Table("WhatsAppOrderSessions")]
    public class OrderSession
    {
        [Key] public string SessionId { get; set; } = Guid.NewGuid().ToString();
        [Required][MaxLength(50)] public string BusinessId { get; set; }
        [Required][Phone] public string PhoneNumber { get; set; }
        [Required] public string CurrentState { get; set; }
        public string ProfileState { get; set; }
        public string CartData { get; set; } = "{}";
        public string PendingParents { get; set; } = "[]";
        public string RevenueCenterId { get; set; }
        public bool TaxExclusive { get; set; }
        public string Email { get; set; }
        public string CustomerName { get; set; }
        public string CollectedName { get; set; }
        public DateTime LastInteraction { get; set; } = DateTime.UtcNow;
        public bool IsEditing { get; set; }
        public string DeliveryMethod { get; set; }
        public string DeliveryAddress { get; set; }
        public string DeliveryContactPhone { get; set; }
        public string DeliveryChargeId { get; set; }
        public string EditGroupsData { get; set; }
        public string EditingGroupId { get; set; }
        public string CurrentPackId { get; set; }
        public string PendingToppings { get; set; } 
        public string PendingToppingsQueue { get; set; } = "[]";
        public string Notes { get; set; }
        public string CurrentMenuLevel { get; set; }
        public string CurrentCategoryGroup { get; set; }
        public string CurrentSubcategoryGroup { get; set; }
        public string HelpEmail { get; set; }
        public string HelpPhoneNumber { get; set; }
        public string DiscountCode { get; set; }
        public decimal DiscountAmount { get; set; }
        public string DiscountType { get; set; }
        public decimal DiscountValue { get; set; }

        [ForeignKey("BusinessId")] public WhatsAppBusiness Business { get; set; }
    }

    [Table("WhatsAppOrders")]
    public class Order
    {
        [Key] public string OrderId { get; set; } = Guid.NewGuid().ToString();
        [Required][MaxLength(50)] public string BusinessId { get; set; }
        [Required][MaxLength(50)] public string RevenueCenterId { get; set; }
        [Required][Phone] public string PhoneNumber { get; set; }
        public string CustomerName { get; set; }
        public DateTime OrderDate { get; set; } = DateTime.UtcNow;
        public decimal Subtotal { get; set; }
        public decimal Tax { get; set; }
        public decimal Charge { get; set; }
        public decimal Total { get; set; }
        public string Status { get; set; }
        public List<OrderItem> Items { get; set; }
        [ForeignKey("BusinessId")] public WhatsAppBusiness Business { get; set; }
    }

    [Table("WhatsAppOrderItems")]
    public class OrderItem
    {
        [Key] public string ItemId { get; set; } = Guid.NewGuid().ToString();
        [Required] public string OrderId { get; set; }
        public string ProductId { get; set; }
        public string ItemName { get; set; }
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public string ComboPartnerId { get; set; }
        public string Toppings { get; set; }
        [ForeignKey("OrderId")] public Order Order { get; set; }
    }
}
