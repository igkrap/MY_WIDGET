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
    }
}
