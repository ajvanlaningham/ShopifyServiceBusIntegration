using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text;
using ShopifySharp;
using System.Collections.Generic;
using ShopifySharp.Lists;
using System.Linq;
using SharedModels;
using ShopifySharp.GraphQL;
using Product = ShopifySharp.Product;
using Microsoft.AspNetCore.Builder;

namespace ShopifyServiceBusIntegration
{
    public static class OrderSender
    {
        [FunctionName("OrderSender")]

        [return: ServiceBus(queueOrTopicName: "orderqueue", Connection = "AzureWebJobsServiceBus")]

        public static async Task<string> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("New Order placed in shopify storefront");

            string shopifyStoreUrl = Environment.GetEnvironmentVariable("ShopURL");
            string apiKey = Environment.GetEnvironmentVariable("APIKey");
            string password = Environment.GetEnvironmentVariable("SecretKey");

            string body = String.Empty;
            using (var reader = new StreamReader(req.Body, Encoding.UTF8))
            {
                body = await OrderProcessTask(shopifyStoreUrl, password, apiKey, log);
                log.LogInformation($"Message Body : {body}");
            }

            log.LogInformation($"SendMessage processed");
            return body;
        }

        private const string _GeneralEcomNumber = "0025616INC";
        private static OrderService _shopifyOrderService;
        private static CustomerService _customerService;
        private static ProductService _productService;
        private static OrderService _orderService;
        private static FulfillmentOrderService _fulfillmentOrderService;
        private static ShopifySharp.Order _Order;

        private enum PhoneNumberComponent
        {
            CountryCode, 
            AreaCode,
            LocalNumber
        }

        private static string _AcctNum = _GeneralEcomNumber;
        private static string _SiteId = "";

        //CONFIRMS THAT ORDER SHOULD BE PROCESSED AT ALL AND CALLS ORDER GENERATION FUNCTION
        private static async Task<string> OrderProcessTask (string shoifyStoreUrl, string password, string apiKey, ILogger log)
        {
            string FinalBodyString = string.Empty;

            _shopifyOrderService = new OrderService(shoifyStoreUrl, password);
            _customerService = new CustomerService(shoifyStoreUrl, password);
            _productService = new ProductService(shoifyStoreUrl, password);
            _orderService = new OrderService(shoifyStoreUrl, password);
            _fulfillmentOrderService = new  FulfillmentOrderService(shoifyStoreUrl, password);

            ListResult<ShopifySharp.Order> orders = await _orderService.ListAsync();
            ShopifySharp.Order order = orders.Items.First();

            _Order = order;

            ShopifySharp.Customer customer = await _customerService.GetAsync(order.Customer.Id.Value);
            _SiteId = await GetSiteUseID(customer.Tags);
            _AcctNum = await GetAccountNumber(customer.Tags);

            if (_AcctNum == _GeneralEcomNumber && order.PaymentGatewayNames.FirstOrDefault() == "Pay by Invoice")
            {
                log.LogInformation("Potential Fraud. Customer requested 'Pay by Invoice' but has Generic account");
                await HoldOrderFulfillment(order, log);
            }
            else
            {
                FinalBodyString = await ProcessOrder(log);
            }

            SharedModels.OrderObject orderObject = JsonConvert.DeserializeObject<OrderObject>(FinalBodyString);
            if (orderObject.OrderLinesList.LineItems.Any())
            {
                return FinalBodyString;
            }
            else
            {
                return null;
            }
        }

        //FINDS CUSTOMER ACCOUNT NUMBER OR ASSIGNS DEFAULT ACCOUNT NUMBER IF NEW ECOMM CUSTOMER
        private async static Task<string> GetAccountNumber(string Tags)
        {
            string[] tags = Tags.Split(',').ToArray();
            foreach (string tag in tags)
            {
                if (tag.StartsWith("AR_"))
                {
                    return tag.Substring(3);
                }
                else if (tag.Contains("INC") || tag.Contains("REF"))
                {
                    return tag;
                }
            }
            return _GeneralEcomNumber;
        }

        //FINDS AND ASSIGNS THE ORACLE "SITE_USE_ID" FOR SHIPPING, IF RETURNING CUSTOMER
        private async static Task<string> GetSiteUseID(string Tags)
        {
            string[] tags = Tags.Split(',').ToArray();
            foreach (string tag in tags)
            {
                if (tag.Contains("SUID"))
                {
                    return tag.Substring(6);
                }
            }
            return "";
        }

        //HOLDS SHOPIFY ORDER WHEN CUSTOMER REQUESTS PAY-BY-INVOICE BUT NO PRE-APPROVED CREDIT ACCOUNT IS KNOWN
        private static async Task HoldOrderFulfillment(ShopifySharp.Order order, ILogger log)
        {
            var fulfillmentOrders = await _fulfillmentOrderService.ListAsync(order.Id.Value);
            log.LogInformation("Current shopify fullfillment orders: " + fulfillmentOrders.ToString());

            try
            {
                await _fulfillmentOrderService.HoldAsync(fulfillmentOrders.FirstOrDefault().Id.Value, new ShopifySharp.FulfillmentHold()
                {
                    Reason = "high_risk_of_fraud",
                    ReasonNotes = "Invoice order but no acct number found"
                });
            }
            catch (Exception ex)
            {
                log.LogInformation($"Error while listing fulfillment orders: {ex}");
            }

            await _orderService.UpdateAsync(order.Id.Value, new ShopifySharp.Order()
            {
                Note = "Error, Invoice order but no acct number found",
                Tags = order.Tags + "InvoiceError",
            });
        }

        private static async Task<string> ProcessOrder(ILogger log)
        {
            OrderObject orderObj = GenerateOrderObject(log);
            var linesList =  new List<SharedModels.Line>();

            foreach (ShopifySharp.LineItem line in _Order.LineItems)
            {
                if (!line.SKU.Contains("Panel"))
                {
                    line.SKU = line.SKU.Split('-').First();
                    double discount = ((double)(Convert.ToDouble(line.DiscountAllocations.FirstOrDefault()?.Amount) / line.Quantity));

                    Product product = await _productService.GetAsync(line.ProductId.Value);

                    SharedModels.LinesList dividedItemList = GetDividedLineItems(line, product);

                    foreach (Line dividedItem in dividedItemList.LineItems)
                    {
                        linesList.Add(dividedItem);
                    }
                }
                else // PANELS/SAMPLE ORDERS ARE HANDLED BY A THIRD PARTY VENDER
                {
                    await _orderService.UpdateAsync(_Order.Id.Value, new ShopifySharp.Order()
                    {
                        Note = "Panel Order," + _Order.Note,
                        Tags = "Panel," + _Order.Tags
                    });
                }
            }

            orderObj.OrderLinesList.LineItems = linesList;

            var json = JsonConvert.SerializeObject(orderObj);
            log.LogInformation($"{json}");

            return json;
        }

        //CONVERTS SHOPIFY ORDER INTO ORACLE ORDER-OBJECT
        private static OrderObject GenerateOrderObject(ILogger log)
        {
            OrderObject orderObj = new OrderObject();
            if (_Order.ShippingAddress.FirstName == null)
            {
                _Order.ShippingAddress.FirstName = "";
            }

            orderObj.CustomerRecord = GenerateCustomerRecord(log);
            orderObj.OrderHeader = GenerateOrderHeader(log);
            orderObj.OrderLinesList = new LinesList();
            orderObj.POU = "US_CNR_OU";

            return orderObj;
        }

        //CONVERTS SHOPIFY-ORDER-INFORMATION INTO ORACLE CUSTOMER RECORD
        private static CustomerRecord GenerateCustomerRecord(ILogger log)
        {
            CustomerRecord record = new CustomerRecord();
            try
            {
                ShopifySharp.Address orderAddress = _Order.ShippingAddress;
                ShopifySharp.Customer customer = _Order.Customer;

                bool hasCompany = (orderAddress.Company != null);
                record = new CustomerRecord()
                {
                    AccountNumber = _AcctNum,
                    SiteUseID = _SiteId,
                    Address1 = GetAddress1(),
                    Address2 = hasCompany ? orderAddress.Company : orderAddress.Address1,
                    Address3 = hasCompany ? orderAddress.Address1 : orderAddress.Address2 ?? "",
                    Address4 = !hasCompany ? orderAddress.Address2 ?? "" : "",
                    City = orderAddress.City,
                    State = orderAddress.Province,
                    PostalCode = orderAddress.Zip,
                    Country = orderAddress.CountryCode,
                    LocationID = "",
                    ContactID = "",
                    ContactFirstName = orderAddress.FirstName,
                    ContactMiddleName = "",
                    ContactLastName = orderAddress.LastName,
                    ContactEmail = customer.Email,
                    PhoneCountryCode = GetPhoneNumberComponent(customer.Phone, PhoneNumberComponent.CountryCode),
                    PhoneAreaCode = GetPhoneNumberComponent(customer.Phone, PhoneNumberComponent.AreaCode),
                    PhoneNumber = GetPhoneNumberComponent(customer.Phone, PhoneNumberComponent.LocalNumber)
                };
            }
            catch (Exception ex)
            {
                log.LogInformation("Error while generating Order Object -- Cutomer record. Error: " + ex.ToString());
            }
            return record;
        }

        //CONVERTS SHOPIFY-ORDER-INFORMATION INTO ORACLE ORDER HEADER
        private static Header GenerateOrderHeader(ILogger log)
        {
            Header header = new Header();
            try
            {
                header = new Header()
                {
                    OrderedDate = _Order.CreatedAt.Value.ToString("yyyy-MM-dd hh:mm:ss"),
                    OrderReferenceID = _Order.Id.Value,
                    CustomerPONumber = _Order.Note != null ? $"{_Order.Name} + {LimitCharacter(_Order.Note, 20)}" : _Order.Name,
                    PaymentTerms = "", // _Order.PaymentGatewayNames.FirstOrDefault() == "Pay by Invoice" ? "net-30" : "net-5"
                    PaymentTypeCode = "",
                    FreightChargesCode = "",
                    FOBPointCode = "",
                    FreightCarrierCode = "",
                    FreightTermsCode = ""
                };
            }
            catch (Exception ex)
            {
                log.LogInformation("Error while generating Order Object -- Header Object. Error: " + ex.ToString());
            }
            return header; 
        }

        //RETURNS ADDRESS ONE IN PREFERRED FORMAT FOR ORACLE, EITHER CUSTOMER'S COMPANY OR CUSTOMER NAME
        private static string GetAddress1()
        {
            string address1 = "";
            if (_Order.BillingAddress.Company != null)
            {
                return address1 = $"*{_Order.BillingAddress.Company.ToUpper()}*";
            }
            else
            {
                return address1 = $"* {_Order.ShippingAddress.FirstName.ToUpper()} {_Order.ShippingAddress.LastName.ToUpper()}*";
            }
        }

        //BREAKS UP PHONE NUMBER INTO COUNTRY-CODE, AREA-CODE, AND LOCAL-NUMBER, THEN RETURNS PER COMPONENT SWITCH
        private static string GetPhoneNumberComponent(string phoneNumber, PhoneNumberComponent component)
        {
            if (string.IsNullOrEmpty(phoneNumber)) return "";

            var numericPhoneNumber = new string(phoneNumber.Where(char.IsDigit).ToArray());

            if (numericPhoneNumber.Length == 10)
            {
                return component switch
                {
                    PhoneNumberComponent.CountryCode => "",
                    PhoneNumberComponent.AreaCode => numericPhoneNumber.Substring(0, 3),
                    PhoneNumberComponent.LocalNumber => numericPhoneNumber.Substring(3),
                    _ => ""
                };
            }
            else if (numericPhoneNumber.Length == 11)
            {
                return component switch
                {
                    PhoneNumberComponent.CountryCode => numericPhoneNumber.Substring(0, 1),
                    PhoneNumberComponent.AreaCode => numericPhoneNumber.Substring(1, 3),
                    PhoneNumberComponent.LocalNumber => numericPhoneNumber.Substring(4),
                    _ => ""
                };
            }

            return "";
        }

        private static string LimitCharacter(string str1, int limit)
        {
            return str1 = str1.Length <= limit ? str1 : str1.Substring(0, limit);
        }

        //TAKES EVERY SHOPIFY ORDER LINE ITEM AND BREAKS IT INTO LARGEST AVAILABLE UNIT-SIZES POSSIBLE TO FULFILL ORDER
        //ADDS EACH UNIT TO THE ORACLE ORDER'S LINE ITEM LIST
        //RETURNS THE ORACLE ORDER LINE ITEM LIST
        private static SharedModels.LinesList GetDividedLineItems(ShopifySharp.LineItem line, Product product)
        {
            SharedModels.LinesList newLinesList = new LinesList();
            var LinesList = new List<SharedModels.Line>();

            bool isDrumSize;

            double discount = ((double)(Convert.ToDouble(line.DiscountAllocations.FirstOrDefault()?.Amount) / line.Quantity));

            List<string> availableBoxExtensions = product.Tags.Split(',')
                .Select(tag => tag.Trim())
                .Where(tag => tag.StartsWith("BX") && IsSupportedBoxExtension(tag))
                .ToList();

            foreach(Line dividedLineItem  in LinesList)
            {
                newLinesList.LineItems.Add(dividedLineItem);
            }
            return newLinesList;
        }


        private static bool IsSupportedBoxExtension(string tag)
        {
            string[] supportedBoxExtensions = { "BXC", "BXA", "BXE", "BXZ", "BXG", "BXH", "BXD" };
            return supportedBoxExtensions.Contains(tag);
        }
    }
}