using System.Text.Json.Serialization;

namespace SharedModels
{
    public class OrderObject
    {
        [JsonPropertyName("custom_rec")]
        public CustomerRecord CustomerRecord { get; set; }

        [JsonPropertyName("header")]
        public Header OrderHeader { get; set; }

        [JsonPropertyName("lines_list")]
        public LinesList OrderLinesList { get; set; }

        [JsonPropertyName("p_ou")]
        public string POU { get; set; }
    }

    public class CustomerRecord
    {
        [JsonPropertyName("cust_acct_num")]
        public string AccountNumber { get; set; }

        [JsonPropertyName("site_use_id")]
        public string SiteUseID { get; set; }

        [JsonPropertyName("address1")]
        public string Address1 { get; set; }

        [JsonPropertyName("address2")]
        public string Address2 { get; set; }

        [JsonPropertyName("address3")]
        public string Address3 { get; set; }

        [JsonPropertyName("address4")]
        public string Address4 { get; set; }

        [JsonPropertyName("city")]
        public string City { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; }

        [JsonPropertyName("country")]
        public string Country { get; set; }

        [JsonPropertyName("postal_code")]
        public string PostalCode { get; set; }

        [JsonPropertyName("location_id")]
        public string LocationID { get; set; }

        [JsonPropertyName("location")]
        public string Location { get; set; }

        [JsonPropertyName("contact_id")]
        public string ContactID { get; set; }

        [JsonPropertyName("contact_first_name")]
        public string ContactFirstName { get; set; }

        [JsonPropertyName("contact_middle_name")]
        public string ContactMiddleName { get; set; }

        [JsonPropertyName("contact_last_name")]
        public string ContactLastName { get; set; }

        [JsonPropertyName("contact_email")]
        public string ContactEmail { get; set; }

        [JsonPropertyName("phone_country_code")]
        public string PhoneCountryCode { get; set; }

        [JsonPropertyName("phone_area_code")]
        public string PhoneAreaCode { get; set; }

        [JsonPropertyName("phone_number")]
        public string PhoneNumber { get; set; }
    }

    public class Header
    {
        [JsonPropertyName("ordered_date")]
        public string OrderedDate { get; set; }

        [JsonPropertyName("orig_sys_document_reference")]
        public long OrderReferenceID { get; set; }

        [JsonPropertyName("cust_po_number")]
        public string CustomerPONumber { get; set; }

        [JsonPropertyName("hdr_payment_terms_code")]
        public string PaymentTerms { get; set; }

        [JsonPropertyName("hdr_payment_type_code")]
        public string PaymentTypeCode { get; set; }

        [JsonPropertyName("hdr_freight_charges_code")]
        public string FreightChargesCode { get; set; }

        [JsonPropertyName("hdr_fob_point_code")]
        public string FOBPointCode { get; set; }

        [JsonPropertyName("hdr_freight_carrier_code")]
        public string FreightCarrierCode { get; set; }

        [JsonPropertyName("hdr_freight_terms_code")]
        public string FreightTermsCode { get; set; }
    }

    public class LinesList
    {
        [JsonPropertyName("line")]
        public List<Line> LineItems { get; set; }
    }

    public class Line
    {
        [JsonPropertyName("unit_price")]
        public string UnitPrice { get; set; }

        [JsonPropertyName("calculate_price_flag")]
        public string CalculatePriceFlag { get; set; }

        [JsonPropertyName("ppg_item_number")]
        public string PPGItemNumber { get; set; }

        [JsonPropertyName("customer_part_number")]
        public object CustomerPartNumber { get; set; }

        [JsonPropertyName("ordered_quantity")]
        public int OrderedQuantity { get; set; }

        [JsonPropertyName("ordered_quantity_uom")]
        public string OrderedQuantityUOM { get; set; }

        [JsonPropertyName("promise_date")]
        public string PromiseDate { get; set; }

        [JsonPropertyName("earliest_acceptable_date")]
        public string EarliestAcceptableDate { get; set; }

        [JsonPropertyName("request_date")]
        public string RequestDate { get; set; }

        [JsonPropertyName("scheduled_ship_date")]
        public string ScheduledShipDate { get; set; }

        [JsonPropertyName("delivery_lead_time")]
        public int DeliveryLeadTime { get; set; }

        [JsonPropertyName("expedited_ship_flag")]
        public string ExpeditedShipFlag { get; set; }

        [JsonPropertyName("freight_carrier_code")]
        public string FreightCarrierCode { get; set; }

        [JsonPropertyName("freight_terms_code")]
        public string FreightTermsCode { get; set; }

        [JsonPropertyName("ship_method_code")]
        public object? ShipMethodCode { get; set; }

        [JsonPropertyName("order_discount")]
        public string OrderDiscount { get; set; }

        [JsonPropertyName("Frieght_Charges_Code")] //at the time of writing, this capitalization is correct
        public string FreightChargesCode { get; set; }

        [JsonPropertyName("fob_point_code")]
        public string FobPointCode { get; set; }

    }
}