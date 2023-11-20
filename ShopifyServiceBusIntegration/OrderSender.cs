using System;
using System.IO;
using System.Threading.Tasks;
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
using Product = ShopifySharp.Product;
using LineItem = ShopifySharp.LineItem;
using System.Globalization;

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
        private static ILogger _logger;

        private enum PhoneNumberComponent
        {
            CountryCode, 
            AreaCode,
            LocalNumber
        }

        private enum SupportedBoxExtension
        {
            BXA, BXC, BXD, BXE, BXG, BXH, BXZ
        }

        private enum SKUTypes
        {
            PCTA, PCTT, PCAT, PCTB, PCST, PCMT, PCFG, PCT, PCM
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
            _logger = log;

            ListResult<ShopifySharp.Order> orders = await _orderService.ListAsync();
            ShopifySharp.Order order = orders.Items.First();

            _Order = order;

            ShopifySharp.Customer customer = await _customerService.GetAsync(order.Customer.Id.Value);
            _SiteId = await GetSiteUseID(customer.Tags);
            _AcctNum = await GetAccountNumber(customer.Tags);

            if (_AcctNum == _GeneralEcomNumber && order.PaymentGatewayNames.FirstOrDefault() == "Pay by Invoice")
            {
                _logger.LogInformation("Potential Fraud. Customer requested 'Pay by Invoice' but has Generic account");
                await HoldOrderFulfillment(order);
            }
            else
            {
                FinalBodyString = await ProcessOrder();
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
        private static async Task HoldOrderFulfillment(ShopifySharp.Order order)
        {
            var fulfillmentOrders = await _fulfillmentOrderService.ListAsync(order.Id.Value);
            _logger.LogInformation("Current shopify fullfillment orders: " + fulfillmentOrders.ToString());

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
                _logger.LogInformation($"Error while listing fulfillment orders: {ex}");
            }

            await _orderService.UpdateAsync(order.Id.Value, new ShopifySharp.Order()
            {
                Note = "Error, Invoice order but no acct number found",
                Tags = order.Tags + "InvoiceError",
            });
        }

        private static async Task<string> ProcessOrder()
        {
            OrderObject orderObj = GenerateOrderObject();
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
            _logger.LogInformation($"{json}");

            return json;
        }

        //CONVERTS SHOPIFY ORDER INTO ORACLE ORDER-OBJECT
        private static OrderObject GenerateOrderObject()
        {
            OrderObject orderObj = new OrderObject();
            if (_Order.ShippingAddress.FirstName == null)
            {
                _Order.ShippingAddress.FirstName = "";
            }

            orderObj.CustomerRecord = GenerateCustomerRecord();
            orderObj.OrderHeader = GenerateOrderHeader();
            orderObj.OrderLinesList = new LinesList();
            orderObj.POU = "US_CNR_OU";

            return orderObj;
        }

        //CONVERTS SHOPIFY-ORDER-INFORMATION INTO ORACLE CUSTOMER RECORD
        private static CustomerRecord GenerateCustomerRecord()
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
                _logger.LogInformation("Error while generating Order Object -- Cutomer record. Error: " + ex.ToString());
            }
            return record;
        }

        //CONVERTS SHOPIFY-ORDER-INFORMATION INTO ORACLE ORDER HEADER
        private static Header GenerateOrderHeader()
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
                _logger.LogInformation("Error while generating Order Object -- Header Object. Error: " + ex.ToString());
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

        //TAKES SHOPIFY ORDER LINE ITEM AND BREAKS IT INTO LARGEST AVAILABLE UNIT-SIZES POSSIBLE TO FULFILL ORDER
        //ADDS EACH UNIT TO THE ORACLE ORDER'S LINE ITEM LIST
        //RETURNS THE ORACLE ORDER LINE ITEM LIST
        private static SharedModels.LinesList GetDividedLineItems(ShopifySharp.LineItem line, Product product)
        {
            SharedModels.LinesList newLinesList = new LinesList();
            var linesList = new List<SharedModels.Line>();

            bool isDrumSize = false;

            double discount = ((double)(Convert.ToDouble(line.DiscountAllocations.FirstOrDefault()?.Amount) / line.Quantity));

            HashSet<SupportedBoxExtension> availableBoxExtensions = AddSupportedBoxExtensions(product); 

            List<ShopifySharp.ProductVariant> variants = product.Variants.ToList();

            foreach (ShopifySharp.ProductVariant variant in variants)
            {
                if (variant.SKU.Contains("/DR"))
                {
                    isDrumSize = true;
                    SharedModels.Line newLine = GenerateSingleLine(line, discount);
                    linesList.Add(newLine);
                }
            }

            if(!isDrumSize && availableBoxExtensions.Any())
            {
                while (line.Quantity > 0)
                {
                    StringBuilder skuExt = new StringBuilder("/", 4);
                    var extensionActions = new Dictionary<SupportedBoxExtension, Action>
                    {
                        {SupportedBoxExtension.BXA, () => skuExt.Append(SupportedBoxExtension.BXA.ToString()) },
                        {SupportedBoxExtension.BXG, () => skuExt.Append(SupportedBoxExtension.BXG.ToString()) },
                        {SupportedBoxExtension.BXH, () => skuExt.Append(SupportedBoxExtension.BXH.ToString()) },
                        {SupportedBoxExtension.BXD, () => skuExt.Append(SupportedBoxExtension.BXD.ToString()) },
                        {SupportedBoxExtension.BXC, () =>
                            {
                                skuExt.Clear();
                                skuExt.Append("/" + SupportedBoxExtension.BXC.ToString());
                            }
                        },
                         {SupportedBoxExtension.BXE, () =>
                            {
                                skuExt.Clear();
                                skuExt.Append("/" + SupportedBoxExtension.BXE.ToString());
                            }
                        },
                          {SupportedBoxExtension.BXZ, () =>
                            {
                                skuExt.Clear();
                                skuExt.Append("/" + SupportedBoxExtension.BXZ.ToString());
                            }
                        }
                    };

                    foreach (SupportedBoxExtension ext in availableBoxExtensions)
                    {
                        if (extensionActions.ContainsKey(ext))
                        {
                            extensionActions[ext].Invoke();
                            line.SKU = skuExt.Insert(0, line.SKU).ToString();
                            Line newLine = GenerateSingleLine(line, discount);
                            linesList.Add(newLine);
                            line.Quantity = line.Quantity - newLine.OrderedQuantity;
                        }
                        else if (availableBoxExtensions.Contains(SupportedBoxExtension.BXC) || 
                                 availableBoxExtensions.Contains(SupportedBoxExtension.BXE) ||
                                 availableBoxExtensions.Contains(SupportedBoxExtension.BXZ))
                        {
                            int partialQuantityMultiplier = 0; //VARIABLE USED TO CALCULATE WHOLE NUMBER MULTIPLES OF BOX SIZES. 
                                                              // EXAMPLE: AN ORDER FOR 275 LBS COULD RESULT IN "5" BOXES OF 50 LBS +
                                                              // 1 BOX OF 25. 335 LBS = "6" 50s, "1" 25, AND "2" 5s
                            while (line.Quantity >= 50)
                            {
                                extensionActions[ext].Invoke();
                                line.SKU = skuExt.Insert(0,line.SKU).ToString();
                                partialQuantityMultiplier = Math.Min(line.Quantity.Value / 50, 50); //BXC = 50 LBS BOX
                                LineItem partialLineItem = GeneratePartialItem(line, partialQuantityMultiplier * 50, skuExt);
                                Line partialOracleLine = GenerateSingleLine(partialLineItem, discount);
                                linesList.Add(partialOracleLine);
                                line.Quantity = line.Quantity - (partialQuantityMultiplier * 50);
                            }
                            availableBoxExtensions.Remove(SupportedBoxExtension.BXC);

                            if (line.Quantity >= 25)
                            {
                                extensionActions[ext].Invoke();
                                line.SKU = skuExt.Insert(0, line.SKU).ToString();
                                partialQuantityMultiplier = 1; //THERE CAN ONLY BE "1" 25 LBS BOX. ANY MORE AND IT WOULD JUST BE HANDLED BY THE 50 LBS BOX
                                LineItem partialLineItem = GeneratePartialItem(line, 25, skuExt);
                                Line partialOracleLine = GenerateSingleLine(partialLineItem, discount);
                                linesList.Add(partialOracleLine);
                                line.Quantity = line.Quantity - (25);
                            }
                            availableBoxExtensions.Remove(SupportedBoxExtension.BXE);

                            while (line.Quantity > 0)
                            {
                                extensionActions[ext].Invoke();
                                line.SKU = skuExt.Insert(0, line.SKU).ToString();
                                partialQuantityMultiplier = Math.Min(line.Quantity.Value / 5, 5); //BXZ = 5 LBS BOX
                                LineItem partialLineItem = GeneratePartialItem(line, partialQuantityMultiplier * 5, skuExt);
                                Line partialOracleLine = GenerateSingleLine(partialLineItem, discount);
                                linesList.Add(partialOracleLine);
                                line.Quantity = line.Quantity - (partialQuantityMultiplier * 5);
                            }
                        }
                    }
                }
            }

            foreach (Line dividedLineItem  in linesList)
            {
                newLinesList.LineItems.Add(dividedLineItem);
            }
            return newLinesList;
        }

        private static LineItem GeneratePartialItem(ShopifySharp.LineItem line, int partialQuantity, StringBuilder skuExt)
        {
            LineItem lineItem = line;
            lineItem.FulfillableQuantity = partialQuantity;
            lineItem.Grams = partialQuantity * 454;
            lineItem.Quantity = partialQuantity;

            return lineItem;
        }

        //ADDS POTENTIAL BOX EXTENSION VALUES TO HASH SET
        private static HashSet<SupportedBoxExtension> AddSupportedBoxExtensions(Product product)
        {
            string sku = product.Variants.FirstOrDefault()?.SKU;

            HashSet<SupportedBoxExtension> extensions = product.Tags.Split(',')
                .Select(tag => tag.Trim())
                .Where(tag => IsSupportedBoxExtension(tag))
                .Select(tag => Enum.Parse<SupportedBoxExtension>(tag.ToUpper()))
                .ToHashSet();

            switch (sku)
            {
                case string s when s.Contains(SKUTypes.PCTA.ToString()):
                    extensions.Add(SupportedBoxExtension.BXE);
                    extensions.Add(SupportedBoxExtension.BXC);
                    extensions.Add(SupportedBoxExtension.BXZ);
                    break;

                case string s when s.Contains(SKUTypes.PCTT.ToString()):
                case string s1 when s1.Contains(SKUTypes.PCAT.ToString()):
                case string s3 when s3.Contains(SKUTypes.PCST.ToString()):
                case string s4 when s4.Contains(SKUTypes.PCMT.ToString()):
                    extensions.Add(SupportedBoxExtension.BXA);
                    break;

                case string s when s.Contains(SKUTypes.PCTB.ToString()):
                    extensions.Add(SupportedBoxExtension.BXH);
                    break;

                case string s when s.Contains(SKUTypes.PCFG.ToString()):
                    extensions.Add(SupportedBoxExtension.BXG);
                    break;
                
                case string s when s.Contains(SKUTypes.PCT.ToString()):
                case string s1 when s1.Contains(SKUTypes.PCM.ToString()):
                    extensions.Add(SupportedBoxExtension.BXE);
                    break;
                
                default:
                    extensions.Add(SupportedBoxExtension.BXA);
                    break;
            }

            return extensions;
        }

        //CREATES A SINGLE ORACLE LINE ITEM FROM DIVIDED SHOPIFY SHARP LINE ITEM
        private static SharedModels.Line GenerateSingleLine(ShopifySharp.LineItem line, double discount)
        {
            SharedModels.Line newLine = new SharedModels.Line();
            try
            {
                newLine = new SharedModels.Line()
                {
                    UnitPrice = Math.Round(Convert.ToDouble(line.Price) - discount, 2).ToString("#.00", CultureInfo.InvariantCulture),
                    CalculatePriceFlag = "N",
                    PPGItemNumber = line.SKU,
                    CustomerPartNumber = line.ProductId,
                    OrderedQuantity = line.Quantity.Value,
                    OrderedQuantityUOM = GetQuantityConverter(Convert.ToInt32(line.Grams.Value)),
                    PromiseDate = "",
                    EarliestAcceptableDate = _Order.CreatedAt.Value.AddDays(5).ToString("yyyy-MM-dd hh:mm:ss"),
                    RequestDate = _Order.CreatedAt.Value.ToString("yyyy-MM-dd hh:mm:ss"),
                    ScheduledShipDate = _Order.CreatedAt.Value.AddDays(2).ToString("yyyy-MM-dd hh:mm:ss"),
                    DeliveryLeadTime = 5,
                    ExpeditedShipFlag = GetExpeditedFlag(),
                    FreightCarrierCode = "GENERIC",
                    FreightTermsCode = null,
                    ShipMethodCode = null,
                    OrderDiscount = "",
                    FreightChargesCode = null,
                    FobPointCode = ""
                };
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"Error while Generating Oracle Order Object -- GenerateSingleLine(item)-- Error: {ex}");
            }
            
            return newLine;
        }

        //PULLS SUPPORTED BOX EXTENSIONS FROM SUPPORTED LIST
        private static bool IsSupportedBoxExtension(string tag)
        {
            tag = tag.ToUpper();
            return Enum.TryParse(tag, out SupportedBoxExtension boxExtension);
        }

        //DEFINES UNIT OF MEASURE FROM GRAMS (DEFAULT IN SHOPIFY) TO LBS (CURRENT DEFAULT IN ORACLE)
        private static string GetQuantityConverter(int grams)
        {
            //TODO: update switch case in case of alternatives/new products
            switch (grams)
            {
                case 454:
                    return "LBS";
            }
            return "LBS";
        }

        //CHECKS ORDER FOR EXPEDITED SHIPPING REQUEST FROM CUSTOMER
        private static string GetExpeditedFlag()
        {
            string flag = "N";
            if (_Order.ShippingLines.Any())
            {
                flag = _Order.ShippingLines.FirstOrDefault().Code.Contains("Standard") ? "N" : "Y";
            }

            return flag;
        }
    }
}