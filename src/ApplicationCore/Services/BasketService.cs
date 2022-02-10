using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Ardalis.GuardClauses;
using Azure.Messaging.ServiceBus;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;
using Newtonsoft.Json;
using static System.Net.Mime.MediaTypeNames;

namespace Microsoft.eShopWeb.ApplicationCore.Services;

public class BasketService : IBasketService
{
    private readonly IRepository<Basket> _basketRepository;
    private readonly IAppLogger<BasketService> _logger;

    // connection string to your Service Bus namespace
    static string connectionString = "Endpoint=sb://eshopwebappservicebus.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=7WKyvIRC74J/Igh6thIC+bIzfj8QdizJQV+OektMDU8=";

    // name of your Service Bus queue
    static string queueName = "orderqueue";

    // the client that owns the connection and can be used to create senders and receivers
    static ServiceBusClient client;

    // the sender used to publish messages to the queue
    static ServiceBusSender sender;

    // number of messages to be sent to the queue
    private const int numOfMessages = 3;
    public BasketService(IRepository<Basket> basketRepository,
        IAppLogger<BasketService> logger,
         IHttpClientFactory httpClientFactory)
    {
        _basketRepository = basketRepository;
        _logger = logger;
    }

    public async Task<Basket> AddItemToBasket(string username, int catalogItemId, decimal price, int quantity = 1)
    {
        var basketSpec = new BasketWithItemsSpecification(username);
        var basket = await _basketRepository.GetBySpecAsync(basketSpec);

        if (basket == null)
        {
            basket = new Basket(username);
            await _basketRepository.AddAsync(basket);
        }

        basket.AddItem(catalogItemId, price, quantity);

        await _basketRepository.UpdateAsync(basket);
        return basket;
    }

    public async Task DeleteBasketAsync(int basketId)
    {
        var basket = await _basketRepository.GetByIdAsync(basketId);
        await _basketRepository.DeleteAsync(basket);
    }

    public async Task<Basket> SetQuantities(int basketId, Dictionary<string, int> quantities)
    {
        Guard.Against.Null(quantities, nameof(quantities));
        var basketSpec = new BasketWithItemsSpecification(basketId);
        var basket = await _basketRepository.GetBySpecAsync(basketSpec);
        Guard.Against.NullBasket(basketId, basket);

        foreach (var item in basket.Items)
        {
            if (quantities.TryGetValue(item.Id.ToString(), out var quantity))
            {
                if (_logger != null) _logger.LogInformation($"Updating quantity of item ID:{item.Id} to {quantity}.");
                item.SetQuantity(quantity);
            }
        }
        basket.RemoveEmptyItems();
        await _basketRepository.UpdateAsync(basket);
        return basket;
    }

    public async Task TransferBasketAsync(string anonymousId, string userName)
    {
        Guard.Against.NullOrEmpty(anonymousId, nameof(anonymousId));
        Guard.Against.NullOrEmpty(userName, nameof(userName));
        var anonymousBasketSpec = new BasketWithItemsSpecification(anonymousId);
        var anonymousBasket = await _basketRepository.GetBySpecAsync(anonymousBasketSpec);
        if (anonymousBasket == null) return;
        var userBasketSpec = new BasketWithItemsSpecification(userName);
        var userBasket = await _basketRepository.GetBySpecAsync(userBasketSpec);
        if (userBasket == null)
        {
            userBasket = new Basket(userName);
            await _basketRepository.AddAsync(userBasket);
        }
        foreach (var item in anonymousBasket.Items)
        {
            userBasket.AddItem(item.CatalogItemId, item.UnitPrice, item.Quantity);
        }
        await _basketRepository.UpdateAsync(userBasket);
        await _basketRepository.DeleteAsync(anonymousBasket);
    }

    public async Task OrderItemsReserver(object obj)
    {
        var itemJson = JsonConvert.SerializeObject(obj);
        client = new ServiceBusClient(connectionString);
        sender = client.CreateSender(queueName);

        // create a batch 
        using ServiceBusMessageBatch messageBatch = await sender.CreateMessageBatchAsync();

        // try adding a message to the batch
        if (!messageBatch.TryAddMessage(new ServiceBusMessage(itemJson)))
        {
            // if it is too large for the batch
            throw new Exception($"The message {itemJson} is too large to fit in the batch.");
        }


        try
        {
            // Use the producer client to send the batch of messages to the Service Bus queue
            await sender.SendMessagesAsync(messageBatch);
        }
        finally
        {
            // Calling DisposeAsync on client types is required to ensure that network
            // resources and other unmanaged objects are properly cleaned up.
            await sender.DisposeAsync();
            await client.DisposeAsync();
        }
    }

}
