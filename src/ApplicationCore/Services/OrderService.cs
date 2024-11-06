using System.Linq;
using System.Threading.Tasks;
using Ardalis.GuardClauses;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;

namespace Microsoft.eShopWeb.ApplicationCore.Services;

public class OrderService : IOrderService
{
    private readonly IRepository<Order> _orderRepository;
    private readonly IUriComposer _uriComposer;
    private readonly IRepository<Basket> _basketRepository;
    private readonly IRepository<CatalogItem> _itemRepository;

    public OrderService(IRepository<Basket> basketRepository,
        IRepository<CatalogItem> itemRepository,
        IRepository<Order> orderRepository,
        IUriComposer uriComposer)
    {
        _orderRepository = orderRepository;
        _uriComposer = uriComposer;
        _basketRepository = basketRepository;
        _itemRepository = itemRepository;
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
    }

    private async Task SendOrderDetailsToFunctionAsync(Order order)
    {
        // Replace with your Azure Function URL
        string functionUrl = "https://orderreserverfunction.azurewebsites.net/api/OrderItemsReserver?code=3yfrHn9_BtQ-GAUvPL7VIn4xcqxpXVdluUDRM0IrGVLLAzFuQtb5eg%3D%3D";

        using (var httpClient = new HttpClient())
        {
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

            string json = JsonSerializer.Serialize(orderDetails);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(functionUrl, content);
            response.EnsureSuccessStatusCode();
        }
    }
}
