using System.Linq;
using System.Threading.Tasks;
using Ardalis.GuardClauses;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;
using System.Net.Http.Json;

namespace Microsoft.eShopWeb.ApplicationCore.Services;

public class OrderService : IOrderService
{
    private readonly IRepository<Order> _orderRepository;
    private readonly IUriComposer _uriComposer;
    private readonly IRepository<Basket> _basketRepository;
    private readonly IRepository<CatalogItem> _itemRepository;
    private readonly ServiceBusClient _serviceBusClient;
    private const string _topicName = "ordercreated";

    public OrderService(
        IRepository<Basket> basketRepository,
        IRepository<CatalogItem> itemRepository,
        IRepository<Order> orderRepository,
        IUriComposer uriComposer)
    {
        _orderRepository = orderRepository;
        _uriComposer = uriComposer;
        _basketRepository = basketRepository;
        _itemRepository = itemRepository;
        _serviceBusClient = new ServiceBusClient("ConnectionString");    // <-- Replace with your Service Bus connection string
    }

    public async Task CreateOrderAsync(int basketId, Address shippingAddress)
    {
        var basketSpec = new BasketWithItemsSpecification(basketId);
        var basket = await _basketRepository.FirstOrDefaultAsync(basketSpec);

        Guard.Against.Null(basket, nameof(basket));
        Guard.Against.EmptyBasketOnCheckout(basket.Items);

        var catalogItemsSpecification = new CatalogItemsSpecification(basket.Items.Select(item => item.CatalogItemId).ToArray());
        var catalogItems = await _itemRepository.ListAsync(catalogItemsSpecification);

        var items = basket.Items.Select(basketItem =>
        {
            var catalogItem = catalogItems.First(c => c.Id == basketItem.CatalogItemId);
            var itemOrdered = new CatalogItemOrdered(catalogItem.Id, catalogItem.Name, _uriComposer.ComposePicUri(catalogItem.PictureUri));
            var orderItem = new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
            return orderItem;
        }).ToList();

        var order = new Order(basket.BuyerId, shippingAddress, items);

        await _orderRepository.AddAsync(order);

        // Send order details to Azure Function
        await SendOrderDetailsToFunctionAsync(order);

        // Send order shipping details to Azure Function
        await SendShippingDetailsToFunctionAsync(order);
    }

    private async Task SendOrderDetailsToFunctionAsync(Order order)
    {
        // Service Bus Topic
        var sender = _serviceBusClient.CreateSender(_topicName);
        var orderDetails = new
        {
            OrderId = order.Id,
            Items = order.OrderItems.Select(item => new
            {
                ItemId = item.ItemOrdered.CatalogItemId,
                ItemName = item.ItemOrdered.ProductName,
                Quantity = item.Units
            })
        };
        var message = new ServiceBusMessage(JsonSerializer.Serialize(orderDetails));
        await sender.SendMessageAsync(message);
    }

    private async Task SendShippingDetailsToFunctionAsync(Order order)
    {
        // Azure Function URL
        string functionUrl = "https://deliveryorderprocessor.azurewebsites.net/api/OrderDeliverySaver?code=JCTB60MoAtT3ao4hwRrh7iwH-8aRpvq8GfNUHz7hn3ooAzFunAw_6g%3D%3D";
        using var client = new HttpClient();
        var payload = new
        {
            OrderId = order.Id,
            ShippingAddress = order.ShipToAddress,
            Items = order.OrderItems.Select(i => new
            {
                ItemId = i.ItemOrdered.CatalogItemId,
                ItemName = i.ItemOrdered.ProductName,
                UnitPrice = i.UnitPrice,
                Quantity = i.Units
            }),
            FinalPrice = order.OrderItems.Sum(i => i.UnitPrice * i.Units)
        };

        string json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync(functionUrl, content);
        response.EnsureSuccessStatusCode();
    }
}
