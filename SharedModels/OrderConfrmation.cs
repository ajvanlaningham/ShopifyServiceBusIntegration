using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SharedModels
{
    public class OrderConfirmation
    {
        [JsonPropertyName("site_id")]
        public int SiteID { get; set; }
        [JsonPropertyName("contact_id")]
        public int ContactID { get; set; }
        [JsonPropertyName("location_id")]
        public int LocationID { get; set; }
        [JsonPropertyName("order_number")]
        public string OrderNumber { get; set; }
        [JsonPropertyName("error_msg")]
        public string? ErrorMsg { get; set; }
    }
}
