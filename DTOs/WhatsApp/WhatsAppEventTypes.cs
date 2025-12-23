using System;
using Newtonsoft.Json.Linq;

namespace FusionComms.DTOs.WhatsApp
{
    public enum WhatsAppEventType
    {
        TextMessage,
        InteractiveButton,
        InteractiveList,
        Image,
        Video,
        Document,
        Audio,
        Sticker,
        Order,
        FlowSubmitted,
        Unknown
    }

    public class WhatsAppMessageEvent
    {
        public string BusinessId { get; set; }
        public string PhoneNumber { get; set; }
        public string CustomerName { get; set; }
        public WhatsAppEventType EventType { get; set; }
        public string Content { get; set; }
        public string InteractivePayload { get; set; }
        public DateTime Timestamp { get; set; }
        public JObject RawMessage { get; set; }
    }
}
