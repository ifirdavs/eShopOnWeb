using System;
using System.IO;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace eshopOrder.Function
{
    public class OrderItemsReserver
    {
        private readonly ILogger<OrderItemsReserver> _logger;

        public OrderItemsReserver(ILogger<OrderItemsReserver> logger)
        {
            _logger = logger;
        }

        [Function("OrderItemsReserver")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            _logger.LogInformation("OrderItemsReserver function processed a request.");

            // Read the request body
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            // Deserialize the JSON into OrderDetails
            OrderDetails orderDetails;
            try
            {
                orderDetails = JsonConvert.DeserializeObject<OrderDetails>(requestBody);
                if (orderDetails == null || orderDetails.Items == null || orderDetails.Items.Count == 0)
                {
                    _logger.LogWarning("Invalid order details received.");
                    return new BadRequestObjectResult("Invalid order details.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing order details.");
                return new BadRequestObjectResult("Invalid JSON format.");
            }

            // Upload order details to Blob Storage
            try
            {
                string connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
                string containerName = "orders";
                string blobName = $"order-{orderDetails.OrderId}-{Guid.NewGuid()}.json";

                BlobContainerClient containerClient = new BlobContainerClient(connectionString, containerName);
                await containerClient.CreateIfNotExistsAsync();

                BlobClient blobClient = containerClient.GetBlobClient(blobName);

                // Serialize order details back to JSON
                string orderJson = JsonConvert.SerializeObject(orderDetails);

                using (MemoryStream ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(orderJson)))
                {
                    await blobClient.UploadAsync(ms, true);
                }

                _logger.LogInformation("Order details uploaded to Blob Storage: {BlobName}", blobName);

                return new OkObjectResult("Order details uploaded successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading order details to Blob Storage.");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
        }
    }

    public class OrderDetails
    {
        public int OrderId { get; set; }
        public List<OrderItem> Items { get; set; }
    }

    public class OrderItem
    {
        public int ItemId { get; set; }
        public string ItemName { get; set; } = "NoName";    // Added to match incoming JSON
        public int Quantity { get; set; }
    }
}
