using System.Text.Json.Serialization;

namespace MangoMaker.Models
{
    public class MarketFill
    {
        [JsonPropertyName("market")]
        public string Market { get; set; }
        
        [JsonPropertyName("queue")]
        public string Queue { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("event")]
        public string Event { get; set; }

        [JsonPropertyName("slot")]
        public ulong Slot { get; set; }

        [JsonPropertyName("write_version")]
        public int WriteVersion { get; set; }
    }
}
