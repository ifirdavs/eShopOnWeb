using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace Company.Function
{
    public class OrderDeliverySaver
    {
        private readonly ILogger<OrderDeliverySaver> _logger;
        private readonly CosmosClient _cosmosClient;

        public OrderDeliverySaver(ILogger<OrderDeliverySaver> logger, CosmosClient cosmosClient)
        {
            _logger = logger;
            _cosmosClient = cosmosClient;
        }

        [Function("OrderDeliverySaver")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            _logger.LogInformation("Saving order to Cosmos DB.");
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            try 
            {
                var orderDetails = JsonSerializer.Deserialize<OrderDeliveryDetails>(requestBody);
                if (orderDetails == null)
                {
                    return new BadRequestObjectResult("Invalid order details.");
                }

                // Add the required id field for Cosmos DB
                orderDetails.id = Guid.NewGuid().ToString();

                var container = _cosmosClient.GetContainer("deliverydb", "orders");
                await container.CreateItemAsync(orderDetails);
                return new OkObjectResult("Order saved to Cosmos DB.");
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error deserializing order details");
                return new BadRequestObjectResult("Invalid JSON format");
            }
        }
    }

    public class OrderDeliveryDetails
    {
        public string id { get; set; } = "";
        public int OrderId { get; set; }
        public Address ShippingAddress { get; set; } = new();
        public List<OrderItem> Items { get; set; } = new();
        public decimal FinalPrice { get; set; }
    }

    public class Address
    {
        public string Street { get; set; } = "";
        public string City { get; set; } = "";
        public string State { get; set; } = "";
        public string Country { get; set; } = "";
        public string ZipCode { get; set; } = "";
    }

    public class OrderItem
    {
        public int ItemId { get; set; }
        public string ItemName { get; set; } = "";
        public decimal UnitPrice { get; set; }
        public int Quantity { get; set; }
    }
}