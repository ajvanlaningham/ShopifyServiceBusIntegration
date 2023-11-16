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


        private static async Task<string> OrderProcessTask (string shoifyStoreUrl, string password, string apiKey, ILogger log)
        {
            string FinalBodyString = string.Empty;

            _shopifyOrderService = new OrderService(shoifyStoreUrl, password);
            _customerService = new CustomerService(shoifyStoreUrl, password);
            _productService = new ProductService(shoifyStoreUrl, password);
            _orderService = new OrderService(shoifyStoreUrl, password);
            _fulfillmentOrderService = new  FulfillmentOrderService(shoifyStoreUrl, password);

            ListResult<ShopifySharp.Order> orders = await _orderService.ListAsync();
            Order order = orders.Items.First();

            _Order = order;

            Customer customer = await _customerService.GetAsync(order.Customer.Id.Value);
            string siteUseId = await GetSiteUseID(customer.Tags);
            string acctNumber = await GetAccountNumber(customer.Tags);

            if (acctNumber == _GeneralEcomNumber && order.PaymentGatewayNames.FirstOrDefault() == "Pay by Invoice")
            {
                log.LogInformation("Potential Fraud. Customer requested 'Pay by Invoice' but has Generic account");
                await HoldOrderFulfillment(order.log);
            }
            else
            {
                FinalBodyString = await ProcessOrder(acctNumber, siteUseId, log);
            }

            SharedModels.Models.OrderObject orderObject = JsonConvert.DeserializeObject<OrderObject>(FinalBodyString);
            if (orderObject.LinesList.Line.Any())
            {
                return FinalBodyString;
            }
            else
            {
                return null;
            }

        }
    }
}