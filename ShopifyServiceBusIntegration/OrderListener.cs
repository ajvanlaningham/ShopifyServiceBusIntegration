using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;

namespace ShopifyServiceBusIntegration
{
    public class OrderListener
    {
        [FunctionName("OrderListener")]
        public void Run([ServiceBusTrigger("errorqueue", Connection = "AzureWebJobsServiceBus")]string myQueueItem, ILogger log)
        {
            log.LogInformation($"C# ServiceBus queue trigger function processed message: {myQueueItem}");
        }
    }
}
