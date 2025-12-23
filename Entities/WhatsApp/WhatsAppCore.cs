using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using FusionComms.Entities.WhatsApp;

namespace FusionComms.Entities.WhatsApp
{
    public class WhatsAppMessage
    {
        [Key] public string MessageId { get; set; } = Guid.NewGuid().ToString();
        [MaxLength(255)] public string ProviderMessageId { get; set; }
        [Required][MaxLength(50)] public string BusinessId { get; set; }
        [MaxLength(100)] public string TemplateId { get; set; }
        [MaxLength(20)] public string MediaId { get; set; }
        [Required][Phone] public string PhoneNumber { get; set; }
        public string Content { get; set; }
        [Required][MaxLength(20)] public string Direction { get; set; }
        [Required][MaxLength(20)] public string Status { get; set; }
        public DateTime Timestamp { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorTitle { get; set; }
        [ForeignKey("BusinessId")] public WhatsAppBusiness Business { get; set; }
        [ForeignKey("TemplateId")] public WhatsAppTemplate Template { get; set; }
        [ForeignKey("MediaId")] public WhatsAppMedia Media { get; set; }
    }

    public class WhatsAppBusiness
    {
        [Key][MaxLength(50)] public string BusinessId { get; set; }
        [Required] public string PhoneNumberId { get; set; }
        [Required] public string AccountId { get; set; }
        [Required] public string BusinessName { get; set; }
        [Required] public string BusinessToken { get; set; }
        [Required] public string AppId { get; set; }
        public string SourceId { get; set; }
        public string RestaurantId { get; set; }
        public string CatalogId { get; set; }
        public string CustomChannelId { get; set; }
        public bool SupportsChat { get; set; } = false;
        public BusinessType BusinessType { get; set; } = BusinessType.Restaurant;
        [MaxLength(50)] public string BotName { get; set; } = "Dyfin";
        public string EmbedlyAccountId { get; set; }
        public string DeliveryFlowJson { get; set; } // Added for WhatsApp Flow integration
    }

    public class WhatsAppMedia
    {
        [Key] public string MediaId { get; set; }
        public string BusinessId { get; set; }
        public string FileName { get; set; }
        public string FileType { get; set; }
        public long FileSize { get; set; }
        public string Caption { get; set; }
        public DateTime UploadTimestamp { get; set; }
        public DateTime ExpirationDate { get; set; }
        [ForeignKey("BusinessId")] public WhatsAppBusiness Business { get; set; }
    }

    public class WhatsAppTemplate
    {
        [Key] public string TemplateId { get; set; }
        [Required] public string TemplateName { get; set; }
        public TemplateCategory Category { get; set; }
        [Required][MaxLength(100)] public string BusinessId { get; set; }
        [MaxLength(20)] public string Status { get; set; }
        [MaxLength(10)] public string Language { get; set; }
        public DateTime CreatedAt { get; set; }
        public int ParameterCount { get; set; }
        public string BodyText { get; set; }
        public string ExampleBodyText { get; set; }
        [ForeignKey("BusinessId")] public WhatsAppBusiness Business { get; set; }
    }

    public enum TemplateCategory
    {
        MARKETING,
        UTILITY,
        AUTHENTICATION,
    }

    public enum BusinessType
    {
        Restaurant,
        Cinema
    }
}
