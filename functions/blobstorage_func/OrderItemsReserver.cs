using System;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text;
using Polly;
using Polly.Retry;
using Newtonsoft.Json;
using Azure.Storage.Blobs;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;


namespace Company.Function
{
    public class OrderItemsReserver
    {
        private readonly AsyncRetryPolicy _retryPolicy;
        private readonly HttpClient _httpClient;
        private readonly ILogger<OrderItemsReserver> _logger;
        private readonly string _logicAppUrl;
        private readonly string? _blobAppSetting;

        public OrderItemsReserver(ILogger<OrderItemsReserver> logger)
        {
            _logger = logger;
            _blobAppSetting = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            _httpClient = new HttpClient();
            _logicAppUrl = Environment.GetEnvironmentVariable("LogicAppUrl") 
                ?? throw new InvalidOperationException("LogicAppUrl environment variable is not set");

            _retryPolicy = Policy
                .Handle<Azure.RequestFailedException>()
                .WaitAndRetryAsync(3, 
                    retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (exception, timeSpan, retryCount, context) =>
                    {
                        _logger.LogWarning("Retry {RetryCount} of {RetryLimit} after {TimeSpan}s delay due to: {Message}",
                            retryCount, 3, timeSpan.TotalSeconds, exception.Message);
                    });
        }

        [Function(nameof(OrderItemsReserver))]
        public async Task<string> Run(
            [ServiceBusTrigger("ordercreated", "orderreserver", Connection = "eshopweb_SERVICEBUS")]
            ServiceBusReceivedMessage message,
            ServiceBusMessageActions messageActions)
        {
            _logger.LogInformation("Processing message: {id}", message.MessageId);
            
            OrderDetails orderDetails;
            var deadLetterProperties = new Dictionary<string, object>
            {
                { "Error", "Invalid order details" },
                { "ErrorTimestamp", DateTime.UtcNow.ToString("o") },
            };
            try
            {
                var messageBody = message.Body.ToString();
                orderDetails = JsonConvert.DeserializeObject<OrderDetails>(messageBody);
                if (orderDetails == null || orderDetails.Items == null || orderDetails.Items.Count == 0)
                {
                    
                    await messageActions.DeadLetterMessageAsync(message, deadLetterProperties);
                    return "Message dead-lettered due to invalid order details";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing message");
                deadLetterProperties["Error"] = "Failed to parse message";
                deadLetterProperties["ErrorMessage"] = ex.Message;
                deadLetterProperties["ErrorTimestamp"] = DateTime.UtcNow.ToString("o");
                await messageActions.DeadLetterMessageAsync(message, deadLetterProperties);
                throw;
            }

            try 
            {
                await _retryPolicy.ExecuteAsync(async () =>
                {
                    await messageActions.RenewMessageLockAsync(message);
                    string blobName = $"order-{orderDetails.OrderId}-{Guid.NewGuid()}.json";
                    BlobServiceClient _blobServiceClient = new BlobServiceClient(_blobAppSetting);
                    var containerClient = _blobServiceClient.GetBlobContainerClient("orders");
                    await containerClient.CreateIfNotExistsAsync();
                    var blobClient = containerClient.GetBlobClient(blobName);
                    
                    string orderJson = JsonConvert.SerializeObject(orderDetails, Formatting.Indented);
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(orderJson));
                    await blobClient.UploadAsync(stream, true);
                    
                    _logger.LogInformation("Order {OrderId} uploaded: {BlobName}", 
                        orderDetails.OrderId, blobName);
                });

                await messageActions.CompleteMessageAsync(message);
                return "Order uploaded successfully";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process order {OrderId}", orderDetails.OrderId);
                
                try
                {
                    // Fallback: Send to Logic App for email notification
                    var failureNotification = new
                    {
                        errorMessage = ex.Message,
                        timestamp = DateTime.UtcNow,
                        orderDetails,
                    };

                    var content = new StringContent(
                        JsonConvert.SerializeObject(failureNotification),
                        Encoding.UTF8,
                        "application/json");

                    var response = await _httpClient.PostAsync(_logicAppUrl, content);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogError("Failed to send failure notification to Logic App");
                    }
                }
                catch (Exception notificationEx)
                {
                    _logger.LogError(notificationEx, "Failed to send failure notification");
                }
                deadLetterProperties["OrderId"] = orderDetails.OrderId;
                deadLetterProperties["ErrorMessage"] = ex.Message;
                deadLetterProperties["ErrorTimestamp"] = DateTime.UtcNow.ToString("o");
                await messageActions.DeadLetterMessageAsync(message, deadLetterProperties);
                throw;
            }
        }
    }

    public class OrderDetails
    {
        public int OrderId { get; set; }
        public required List<OrderItem> Items { get; set; }
    }

    public class OrderItem
    {
        public int ItemId { get; set; }
        public string ItemName { get; set; } = "NoName";    // Added to match incoming JSON
        public int Quantity { get; set; }
    }
}
