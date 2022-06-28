using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using MangoMaker.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Solnet.Mango;
using Solnet.Mango.Models;
using Solnet.Mango.Models.Events;
using Solnet.Mango.Models.Perpetuals;
using Solnet.Mango.Types;
using Solnet.Programs.Models;
using Solnet.Rpc;
using Solnet.Rpc.Builders;
using Solnet.Rpc.Core.Http;
using Solnet.Rpc.Models;
using Solnet.Rpc.Types;
using Solnet.Serum.Models;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;
using Websocket.Client;
using Account = Solnet.Wallet.Account;
using ClientFactory = Solnet.Mango.ClientFactory;
using OpenOrder = Solnet.Mango.Models.Matching.OpenOrder;
using TokenInfo = Solnet.Mango.Models.TokenInfo;

namespace MangoMaker
{
    public partial class MakerService
    {
        private ClientWebSocket _mangoClientWebSocket = new();
        private readonly IRpcClient _rpcClient;
        private static IStreamingRpcClient _streamingRpcClient;
        private static IMangoClient _mangoClient;
        private readonly AccountResultWrapper<PerpMarket> _perpMarket;
        private readonly List<Subscription> _mangoSubscriptions = new();
        private readonly Dictionary<Side, CustomMangoBookSide> _mangoOrderBook = new();
        private readonly string _mangoStreamingClientUrl;
        private readonly Dictionary<Side, CustomOrder> _mangoOrders = new();
        private CustomOrder _mangoReduceOrder;
        private readonly long _mangoOrderSize;
        private readonly PublicKey _solanaWalletPublicKey;
        readonly AccountResultWrapper<MangoGroup> _mangoGroup;
        private readonly PublicKey _mangoAccountPublicKey;
        private readonly List<CustomFill> _mangoFills = new();        
        private readonly Wallet _mangoWallet;
        private readonly List<PublicKey> _mangoSpotOrders;
        private decimal _microLots;
        private long _mangoPositionSize;
        private bool _gettingBlockHash;
        private readonly MangoProgram _mangoProgram;
        private readonly int _marketIndex;
        private readonly TokenInfo _token;
        private readonly TokenInfo _quoteToken;
        private readonly int _mangoRequestTimeout = 5000;
        private readonly int _mangoPriceDecimalPlaces;

        private async Task ConnectMangoFillFeed()
        {
            // Connect to Mango fills WebSocket
            try
            {
                ManualResetEvent exitEvent = new(false);
                Uri url = new Uri(_fillFeedUrl);

                _logger.LogInformation($"Connecting to Mango fill data feed...");
                using (WebsocketClient client = new(url))
                {
                    client.ReconnectTimeout = TimeSpan.FromSeconds(60);
                    client.ReconnectionHappened.Subscribe(info =>
                    {
                        _logger.LogInformation($"Connected to Mango fill data feed, {info.Type}.");
                    });

                    client.MessageReceived.Subscribe(async json =>
                    {
                        // Deserialise fill data
                        MarketFill? marketFill = JsonConvert.DeserializeObject<MarketFill>(json.ToString());
                        if (marketFill != null &&
                            marketFill.Event != null &&
                            marketFill.Market == _symbol
                        )
                        {
                            byte[] fillBytes = Convert.FromBase64String(marketFill.Event);
                            FillEvent fillEvent = FillEvent.Deserialize(fillBytes);

                            // Process fill
                            if (marketFill.Status == "New")
                            {
                                await ProcessMangoFill(fillEvent, false);
                            }
                            else if (marketFill.Status == "Revoke")
                            {
                                fillEvent.TakerSide = fillEvent.TakerSide.OtherSide();
                                await ProcessMangoFill(fillEvent, true);
                            }
                        }
                    });
                    await client.Start();
                    exitEvent.WaitOne();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"{ex}");
            }
        }

        private async Task MangoSubscribe()
        {
            bool connectedAndSubscribed = false;

            while (!connectedAndSubscribed)
            {
                try
                {
                    if (_mangoClientWebSocket.State == WebSocketState.Aborted || _mangoClientWebSocket.State == WebSocketState.Closed)
                    {
                        _mangoClient.StreamingRpcClient.ConnectionStateChangedEvent -= StreamingRpcClientOnConnectionStateChangedEvent;
                        _logger.LogInformation($"WebSocket state: {_mangoClientWebSocket.State}, reconnecting.");
                        _mangoClientWebSocket.Dispose();
                        _mangoClientWebSocket = new();
                        _streamingRpcClient = Solnet.Rpc.ClientFactory.GetStreamingClient(_mangoStreamingClientUrl, null, _mangoClientWebSocket);
                        _mangoClient = ClientFactory.GetClient(_rpcClient, _streamingRpcClient);
                    }
                    else
                    {
                        await _mangoClient.StreamingRpcClient.ConnectAsync();
                    }

                    if (_mangoClientWebSocket.State == WebSocketState.Open)
                    {
                        _mangoClient.StreamingRpcClient.ConnectionStateChangedEvent += StreamingRpcClientOnConnectionStateChangedEvent;
                        
                        async Task SubscribeToBids()
                        {
                            _mangoSubscriptions.Add(await _mangoClient.SubscribeOrderBookSideAsync(async (_, side, _) =>
                            {
                                _mangoOrderBook[Side.Buy] = new CustomMangoBookSide()
                                {
                                    OpenOrders = side.GetOrders(),
                                    LastUpdated = DateTime.UtcNow
                                };

                                await EnqueueOrdersOnMango(Side.Buy);

                            }, _perpMarket.ParsedResult.Bids, Commitment.Processed));
                        }

                        async Task SubscribeToAsks()
                        {
                            _mangoSubscriptions.Add(await _mangoClient.SubscribeOrderBookSideAsync(async (_, side, _) =>
                            {
                                _mangoOrderBook[Side.Sell] = new CustomMangoBookSide()
                                {
                                    OpenOrders = side.GetOrders(),
                                    LastUpdated = DateTime.UtcNow
                                };

                                await EnqueueOrdersOnMango(Side.Sell);

                            }, _perpMarket.ParsedResult.Asks, Commitment.Processed));
                        }

                        // Subscribe to bids and asks at the same time
                        _logger.LogInformation("Connecting to Mango order book data feed...");
                        await Task.WhenAll(new List<Task> { SubscribeToBids(), SubscribeToAsks() });
                        _logger.LogInformation("Connected to Mango order book data feed.");
                        connectedAndSubscribed = true;
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError("Error subscribing to Mango data feeds. " + e.Message);
                    await Task.Delay(1000);
                }
            }
        }

        private async void StreamingRpcClientOnConnectionStateChangedEvent(object? sender, WebSocketState e)
        {
            // Reconnect to RPC WebSocket on disconnect
            if (((IStreamingRpcClient)sender).State != WebSocketState.Open ||
                   ((IStreamingRpcClient)sender).State != WebSocketState.Connecting ||
                   ((IStreamingRpcClient)sender).State != WebSocketState.None)
            {
                await MangoSubscribe();
            }
        }

        private async Task EnqueueOrdersOnMango(Side side)
        {
            // If there is no open position on Mango, return.
            if (side == Side.Buy && _mangoPositionSize < _maximumPositionSize ||
                side == Side.Sell && _mangoPositionSize > (_maximumPositionSize * -1))
            {
                // Order Book empty
                if (!_mangoOrderBook.ContainsKey(Side.Buy) || !_mangoOrderBook.ContainsKey(Side.Sell))
                {
                    return;
                }
             
                decimal midPrice = decimal.Divide(_hedgeService.BestPrice[Side.Buy]() + _hedgeService.BestPrice[Side.Sell](), 2);
                decimal quotePrice = side == Side.Buy ? midPrice * (1m - _spreadBps) :
                                                        midPrice * (1m + _spreadBps);                

                (long rawPrice, _) = _perpMarket.ParsedResult.UiToNativePriceQuantity((double)Math.Round(quotePrice, _mangoPriceDecimalPlaces), 0, _token.Decimals, _quoteToken.Decimals);

                //get open orders
                AccountResultWrapper<MangoAccount> mangoAccount = await GetSingleAccountAsync(_mangoClient.GetMangoAccountAsync(_mangoAccountPublicKey, Commitment.Processed), _logger);
                if (mangoAccount != null)
                {
                    long orderSize = Math.Min(_mangoOrderSize, Math.Max(0, _maximumPositionSize - Math.Abs(_mangoPositionSize)));

                    if (orderSize > 0)
                    {
                        List<PerpetualOpenOrder> existingOrders = mangoAccount.ParsedResult
                        .GetOrders()
                        .Where(o => o.Side == side &&
                                    o.MarketIndex == _marketIndex)
                        .ToList();

                        if (existingOrders.Count < 1)
                        {
                            _mangoOrders[side] = new CustomOrder()
                            {
                                Side = side,
                                RawPrice = rawPrice,
                                RawQuantity = orderSize,
                                ReduceOnly = false
                            };

                            await PlaceMangoOrders();
                        }
                        else
                        {
                            List<OpenOrder> openOrders = _mangoOrderBook[side].OpenOrders.Where(o => existingOrders.Any(po => po.OrderId == o.OrderId)).ToList();

                            if (openOrders.Count > 0)
                            {
                                // Define corridor
                                decimal buyUpperLimit = Math.Round(midPrice * (1m - decimal.Divide(_spreadBps, 2)), _mangoPriceDecimalPlaces);
                                decimal buyLowerLimit = Math.Round(midPrice * (1m - (_spreadBps * 2)), _mangoPriceDecimalPlaces);
                                decimal sellLowerLimit = Math.Round(midPrice * (1m + decimal.Divide(_spreadBps, 2)), _mangoPriceDecimalPlaces);
                                decimal sellUpperLimit = Math.Round(midPrice * (1m + (_spreadBps * 2)), _mangoPriceDecimalPlaces);

                                if (openOrders.All(openOrder =>
                                {
                                    decimal openOrderPrice = _perpMarket.ParsedResult.PriceLotsToNumber(new I80F48(openOrder.RawPrice), _token.Decimals, _quoteToken.Decimals);

                                    return side == Side.Buy && (openOrderPrice > buyUpperLimit || openOrderPrice < buyLowerLimit) ||
                                           side == Side.Sell && (openOrderPrice < sellLowerLimit || openOrderPrice > sellUpperLimit);
                                }) && orderSize != 0)
                                {
                                    _mangoOrders[side] = new CustomOrder()
                                    {
                                        Side = side,
                                        RawPrice = rawPrice,
                                        RawQuantity = orderSize,
                                        ReduceOnly = false
                                    };

                                    await PlaceMangoOrders();
                                }
                            }
                            else
                            {
                                _mangoOrders[side] = new CustomOrder()
                                {
                                    Side = side,
                                    RawPrice = rawPrice,
                                    RawQuantity = _mangoOrderSize,
                                    ReduceOnly = false
                                };

                                await PlaceMangoOrders();
                            }
                        }
                    }
                }
            }
        }

        private async Task UnhedgeMango()
        {
            // If there are open unfilled orders waiting to be hedged
            if (_hedgeService.UnhedgedQuantity != 0)
            {
                return;
            }

            // If there is no position on waiting to be hedged or on mango
            if (_mangoPositionSize == 0 && _hedgeService.HedgedPositionSize == 0 && _microLots == 0)
            {
                _mangoReduceOrder = null;
                return;
            }

            Side side = _mangoPositionSize > 0 ? Side.Sell : Side.Buy;
            (long cexMidPrice, _) = _perpMarket.ParsedResult.UiToNativePriceQuantity((double)Math.Round((_hedgeService.BestPrice[Side.Buy]() + _hedgeService.BestPrice[Side.Sell]()) / 2, 2), 0, _token.Decimals, _quoteToken.Decimals);

            if (_mangoPositionSize != 0)
            {
                _mangoReduceOrder = new CustomOrder()
                {
                    Side = side,
                    RawPrice = cexMidPrice,
                    RawQuantity = Math.Abs(_mangoPositionSize),
                    ReduceOnly = true
                };
            }
            else
            {
                _mangoReduceOrder = null;
            }

            await PlaceMangoOrders();
        }

        private async Task<long> GetMangoPosition()
        {
            for (int i = 0; i < 5; i++)
            {
                AccountResultWrapper<MangoAccount> mangoAccount = await _mangoClient.GetMangoAccountAsync(_mangoAccountPublicKey, Commitment.Finalized);
                if (!mangoAccount.WasSuccessful)
                {
                    _logger.LogError($"Error getting Mango position. {mangoAccount.OriginalRequest.HttpStatusCode}: {mangoAccount.OriginalRequest.Reason}");
                    await Task.Delay(500);
                }
                else if (mangoAccount.WasSuccessful)
                {
                    return mangoAccount.ParsedResult.PerpetualAccounts[_marketIndex].BasePosition;
                }
            }

            return 0;
        }

        private async Task ProcessMangoFill(FillEvent fill, bool revoke)
        {
            // If the fill occurred after the app started and the fill is for this account 
            if (DateTimeOffset.FromUnixTimeSeconds((long)fill.Timestamp).DateTime >= _startTime &&
                fill.Maker.Key == _mangoAccountPublicKey.Key)
            {
                DateTime notifiedTimestamp = DateTime.UtcNow;

                // Update Mango position in memory
                _mangoPositionSize += fill.Quantity * (fill.TakerSide == Side.Buy ? -1 : 1);

                string fillMsg = revoke ? "REVOKE" : "FILL";

                fillMsg += $" - [{_symbol}], " +
                           $"SequenceNumber = {fill.SequenceNumber}, " +
                           $"Quantity = {fill.Quantity}, " +
                           $"TakerSide = {fill.TakerSide}, " +
                           $"MakerSlot = {fill.MakerSlot}, " +
                           $"MakerOut = {fill.MakerOut}, " +
                           $"Maker = {fill.Maker.Key}, " +
                           $"MakerOrderId = {fill.MakerOrderId}, " +
                           $"MakerClientOrderId = {fill.MakerClientOrderId}, " +
                           $"MakerFee = {fill.MakerFee.ToDecimal()}, " +
                           $"BestInitial = {fill.BestInitial}, " +
                           $"MakerTimestamp = {fill.MakerTimestamp}, " +
                           $"Taker = {fill.Taker.Key}, " +
                           $"TakerOrderId = {fill.TakerOrderId}, " +
                           $"TakerClientOrderId = {fill.TakerClientOrderId}, " +
                           $"TakerFee = {fill.TakerFee.ToDecimal()}, " +
                           $"Price = {fill.Price}";

                _logger.LogInformation(fillMsg);

                decimal price = _perpMarket.ParsedResult.PriceLotsToNumber(new I80F48(fill.Price), _token.Decimals, _quoteToken.Decimals);
                decimal quantity = _perpMarket.ParsedResult.BaseLotsToNumber(fill.Quantity, _token.Decimals);                

                // Remove orders from next update
                _mangoOrders.Remove(fill.TakerSide.OtherSide());

                // Cancel all open orders
                _ = CancelMangoOrders(false); // no await

                for (; ; )
                {
                    decimal qty = _perpMarket.ParsedResult.BaseLotsToNumber(fill.Quantity, _token.Decimals) * (fill.TakerSide == Side.Buy ? -1 : 1);
                    decimal tempQty = qty + _microLots;
                    decimal nQty = TruncateDecimal(tempQty, _cexQuantityDecimalPlaces);

                    if (nQty != 0 && _hedgeTrades)
                    {
                        await _hedgeService.Enqueue(fill.TakerSide, Math.Abs(nQty));

                        _microLots = tempQty - nQty; // Balance micro lots on fill

                        // Add fill to memory
                        CustomFill mambaFill = new CustomFill()
                        {
                            Fill = fill,
                            Hedged = false,
                            FillNotifiedTimestamp = notifiedTimestamp
                        };
                        _mangoFills.Add(mambaFill);

                        break;

                    }
                    else
                    {
                        _microLots = tempQty - nQty; // balance micro lots on micro fill

                        // Add fill to memory
                        CustomFill mambaFill = new CustomFill()
                        {
                            Fill = fill,
                            Hedged = true,
                            FillNotifiedTimestamp = notifiedTimestamp,
                            HedgeConfirmedTimestamp = DateTime.UtcNow
                        };
                        _mangoFills.Add(mambaFill);

                        break;
                    }
                }
            }
        }

        private async Task CancelMangoOrders(bool confirm)
        {
            _logger.LogInformation("Cancelling orders on Mango...");
            for (; ; )
            {
                List<TransactionInstruction> instructions = new List<TransactionInstruction>
                    {
                        _mangoProgram.CancelAllPerpOrders(
                            Constants.MangoGroup,
                            _mangoAccountPublicKey,
                            _solanaWalletPublicKey,
                            _mangoGroup.ParsedResult.PerpetualMarkets[_marketIndex].Market,
                            _perpMarket.ParsedResult.Bids,
                            _perpMarket.ParsedResult.Asks,
                            3
                        )
                    };

                TransactionBuilder txBuilder = await BuildTransactionAsync(instructions);

                byte[] txBytes = txBuilder.Build(new List<Account>() { _mangoWallet.Account });
                string transactionHash = Encoders.Base58.EncodeData(txBytes.Skip(1).Take(64).ToArray());

                RequestResult<string> res = await _rpcClient.SendTransactionAsync(txBytes, true, Commitment.Finalized);

                if (!res.WasSuccessful)
                {
                    _logger.LogError($"Error cancelling orders on Mango. {res.ServerErrorCode}: {res.Reason}");
                    await Task.Delay(500);
                }
                else
                {
                    if (confirm)
                    {
                        AccountResultWrapper<MangoAccount> mangoAccount = await GetSingleAccountAsync(_mangoClient.GetMangoAccountAsync(_mangoAccountPublicKey, Commitment.Finalized), _logger);
                        if (mangoAccount != null)
                        {
                            List<PerpetualOpenOrder> potentialOrders = mangoAccount.ParsedResult.GetOrders();

                            if (!potentialOrders.Any(o => o.MarketIndex == _marketIndex))
                            {
                                _logger.LogInformation("Mango orders cancelled successfully.");
                                break;
                            }
                        }
                        else
                        {
                            _logger.LogError($"Error getting Mango accounts.");
                            await Task.Delay(500);
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        public AccountResultWrapper<T> GetSingleAccount<T>(Task<AccountResultWrapper<T>> task, ILogger logger)
        {
            // Retry getting a mango account 5 times a timeout
            for (int i = 0; i < 5; i++)
            {
                if (Task.WhenAny(task, Task.Delay(_mangoRequestTimeout)).Result == task)
                {
                    AccountResultWrapper<T> result = task.Result;

                    if (!result.WasSuccessful)
                    {
                        logger.LogError($"Error getting single account from Mango. {result.OriginalRequest.ServerErrorCode}: {result.OriginalRequest.Reason}.");
                        Task.Delay(500).Wait();
                    }
                    else
                    {
                        return result;
                    }
                }
                else
                {
                    logger.LogError("Timeout getting single account from Mango.");
                }
            }
            return null;
        }

        public async Task<AccountResultWrapper<T>> GetSingleAccountAsync<T>(Task<AccountResultWrapper<T>> task, ILogger logger)
        {
            // Retry getting a mango account 5 times a timeout
            for (int i = 0; i < 5; i++)
            {
                if (await Task.WhenAny(task, Task.Delay(_mangoRequestTimeout)) == task)
                {
                    AccountResultWrapper<T> result = await task;
                    if (!result.WasSuccessful)
                    {
                        logger.LogError($"Error getting single account from Mango. {result.OriginalRequest.ServerErrorCode}: {result.OriginalRequest.Reason}.");
                        await Task.Delay(500);
                    }
                    else
                    {
                        return result;
                    }
                }
                else
                {
                    logger.LogError("Timeout getting single account from Mango.");
                }
            }
            return null;
        }
    }
}