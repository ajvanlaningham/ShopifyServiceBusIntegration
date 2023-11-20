using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SharedModels;
using ShopifySharp;
using ShopifySharp.Lists;

namespace ShopifyServiceBusIntegration
{
    public class OrderListener
    {
        [FunctionName("OrderListener")]
        public async Task Run([ServiceBusTrigger("errorqueue", Connection = "AzureWebJobsServiceBus")]string myQueueItem, ILogger log)
        {
            log.LogInformation($"C# ServiceBus queue trigger function processed message: {myQueueItem}");
            try
            {
                string shopifyStoreUrl = Environment.GetEnvironmentVariable("ProdShopUrl");
                string apiKey = Environment.GetEnvironmentVariable("ProdAPIKey");
                string password = Environment.GetEnvironmentVariable("ProdSecretKey");
                OrderConfirmation orderUpdate = JsonConvert.DeserializeObject<OrderConfirmation>(myQueueItem);

                await ProcessMessageAsync(orderUpdate, log, shopifyStoreUrl, password);
                string Message = orderUpdate.OrderNumber;
                await SendFlowMessageAsync(Message);
            }
            catch (Exception ex) 
            {
                log.LogInformation($"Error while processing Service Bus Queue Message -- Error: {ex}"); 
            }
        }

        //SENDS A MESSAGE TO A POWER AUTOMATE FLOW, WHICH IN TURNS SENDS AN EMAIL AND TEAMS MESSAGE THAT A NEW ORDER HAS BEEN PROCESSED AND IS WAITING FOR REVIEW
        private async Task SendFlowMessageAsync(string message)
        {
            try
            {
                _logger.LogInformation("Message to Power Automate Flow: " + message);

                var httpClient = new HttpClient();
                var request = new HttpRequestMessage(HttpMethod.Post, Environment.GetEnvironmentVariable("PowerAutomateAddress"));

                request.Content = new StringContent(message, System.Text.Encoding.UTF8, "text/plain");
                var response = await httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Message to Flow successful");
                }
                else
                {
                    _logger.LogInformation("Message to flow unsuccessfull");
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"Error: {ex}");
            }
        }

        private static OrderService _orderService;
        private static CustomerService _customerService;
        private static FulfillmentOrderService _fulfillmentOrderService;
        private static ILogger _logger;
        private const string _GeneralEcomNumber = "0025616INC";

        private enum AccountBizPrefix
        {
            REF, INC
        }

        //FINDS THE CORRECT ORDER WITHIN THE MOST RECENT 50 ORDERS PLACED IN A SHOPIFY STOREFRONT
        //INTERPRETS SERVICE BUS MESSAGE FOR SUCCESS/FAILURE
        private static async Task ProcessMessageAsync(OrderConfirmation orderUpdate, ILogger log, string storeUrl, string password)
        {
            _orderService = new OrderService(storeUrl, password);
            _customerService = new CustomerService(storeUrl, password);
            _fulfillmentOrderService = new FulfillmentOrderService(storeUrl, password);
            _logger = log;
            try
            {
                ListResult<Order> orders = await _orderService.ListAsync();
                Order order = orders.Items.ToList().Find(ord => ord.Tags.Contains(orderUpdate.OrderNumber));
                Customer customer = order.Customer;

                if (orderUpdate.ErrorMsg != null || orderUpdate.ErrorMsg != "")
                {
                    await UpdateOrderSuccessAsync(orderUpdate, order, customer);
                }
                else
                {
                    _logger.LogInformation($"An Error was reported from Oracle-- Error Message: {orderUpdate.ErrorMsg}");
                    order.Note = "Oracle error: " + orderUpdate.ErrorMsg;
                    HoldOrderFulfillmentAsync(order);
                }
            }
            catch (Exception ex) 
            {
                _logger.LogInformation(ex.ToString());            
            }
        }

        //UPDATES SHOPIFY ORDER WITH NEW INFORMATION OBTAINED FROM SERVICE BUS MESSAGE
        //UPDATES CUSTOMER WITH SITE-ID VALUE SO THAT FUTURE ORDERS WILL GO TO CORRECT LOCATION WITHOUT CREATING NEW ONE IN ORACLE
        private static async Task UpdateOrderSuccessAsync(OrderConfirmation orderUpdate, Order order, Customer customer)
        {
            StringBuilder oracleOrderNumber = new StringBuilder(orderUpdate.OrderNumber, orderUpdate.OrderNumber.Length + 10);
            StringBuilder siteID = new StringBuilder($"SUID_{orderUpdate.SiteID}", 10);
            string acctNum = GetAccountNumber(customer.Tags);

            order.Note = order.Note == null ? acctNum : order.Note + "," + acctNum;
            order.Tags = order.Tags == ""
                ? oracleOrderNumber.Append(siteID.ToString()).ToString()
                : order.Tags + "," + oracleOrderNumber.Append(siteID.ToString()).ToString();

            if(!customer.Tags.Contains(orderUpdate.SiteID.ToString()))
            {
                await _customerService.UpdateAsync(customer.Id.Value, new Customer()
                {
                    Tags = customer.Tags == ""
                    ? siteID.ToString()
                    : customer.Tags + siteID.Insert(0, ",").ToString()
                });

                _logger.LogInformation($"Cutomer updated in Shopify with new Site Use ID number: {orderUpdate.SiteID}.");
            }

            await _orderService.UpdateAsync(order.Id.Value, order);
            _logger.LogInformation("Order updated in shopify.");
        }

        //IF ERROR RETURNED, HOLDS ORDER FULFILLMENT IN SHOPIFY TO ALERT SHOP OWNER/ADMIN
        //UPDATES ORDER WITH THE ERROR MESSAGE RETURNED FROM ORACLE
        private static async void HoldOrderFulfillmentAsync(Order order)
        {
            var fulfillmentOrders = await _fulfillmentOrderService.ListAsync(order.Id.Value);

            await _fulfillmentOrderService.HoldAsync(fulfillmentOrders.FirstOrDefault().Id.Value, new FulfillmentHold()
            {
                Reason = "other",
                ReasonNotes = "error returned from oracle integration"
            });

            await _orderService.UpdateAsync(order.Id.Value, new Order()
            {
                Note = "Error returned from oracle integration: " + order.Note,
                Tags = order.Tags + ",InvoiceError"
            });
        }

        //GETS ACCOUNT NUMBER FROM CUSTOMER SHOPIFY PROFILE OR USES DEFAULT
        private static string GetAccountNumber(string tags)
        {
            string[] tagArray = tags.Split(',');

            string arTag = tagArray.FirstOrDefault(tag => tag.StartsWith("AR_"));
            if (!string.IsNullOrEmpty(arTag))
            {
                return arTag.Substring(3);
            }

            string incOrRefTag = tagArray.FirstOrDefault(tag => tag.Contains(AccountBizPrefix.INC.ToString()) || tag.Contains(AccountBizPrefix.REF.ToString()));
            if (!string.IsNullOrEmpty(incOrRefTag))
            {
                return incOrRefTag;
            }

            return _GeneralEcomNumber;
        }
    }
}
