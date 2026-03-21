using System.Runtime.Serialization;

namespace TodoWidget.Models
{
    [DataContract]
    public class WidgetSettings
    {
        [DataMember(Order = 1)]
        public double Opacity { get; set; }

        [DataMember(Order = 2)]
        public bool IsTopmost { get; set; }

        [DataMember(Order = 3)]
        public bool TelegramEnabled { get; set; }

        [DataMember(Order = 4)]
        public string TelegramBotToken { get; set; }

        [DataMember(Order = 5)]
        public string TelegramChatId { get; set; }
    }
}
