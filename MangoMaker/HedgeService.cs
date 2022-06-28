using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Solnet.Rpc;
using Solnet.Serum;
using Solnet.Serum.Models;
using Solnet.Wallet;
using ClientFactory = Solnet.Serum.ClientFactory;
using System.Net.WebSockets;
using Solnet.Programs.Models;
using Solnet.Rpc.Types;
using Solnet.Rpc.Builders;
using Solnet.Rpc.Models;
using Solnet.Mango;
using Solnet.Mango.Models;
using System.Numerics;
using Solnet.Mango.Models.Banks;
using Solnet.Programs;
using Microsoft.Extensions.Caching.Memory;
using Solnet.Wallet.Utilities;
using Solnet.Rpc.Messages;

namespace MangoMaker
{
    public class HedgeService : IHostedService, IDisposable
    {
        private readonly ILogger<HedgeService> _logger;
        private Timer _timer = null!;
        public decimal UnhedgedQuantity;
        private readonly string _serumSymbol;
        public readonly Dictionary<Side, Func<decimal>> BestPrice = new();
        private ISerumClient _serumClient;
        private IStreamingRpcClient _streamingRpcClient;
        private readonly IRpcClient _rpcClient;        
        private Wallet _wallet;
        private readonly Market _serumMarket;
        public event Func<object, EventArgs, Task> PriceUpdate;
        public decimal HedgedPositionSize;
        private ClientWebSocket _serumClientWebSocket = new();        
        private readonly List<Subscription> _serumSubscriptions = new();
        private readonly Dictionary<Side, List<OpenOrder>> _serumOrderBook = new();
        private readonly SerumProgram _serumProgram;

        private IMangoClient _mangoClient;
        private MangoProgram _mangoProgram;
        private MangoGroup _mangoGroup;
        private PublicKey _mangoAccountPublicKey;
        private PublicKey _solanaWalletPublicKey;
        private List<Account> _signers;
        private MangoAccount _mangoAccount;
        private bool _gettingBlockHash = false;
        private readonly IMemoryCache _cache;
        private Market _market;
        private int _marketIndex;
        private ulong _serumPriceDecimalPlaces;              

        public HedgeService(ILogger<HedgeService> logger, IMemoryCache cache)
        {
            try
            {
                _logger = logger;
                _cache = cache;
                _signers = new();                
                _serumProgram = SerumProgram.CreateMainNet();
                _rpcClient = Solnet.Rpc.ClientFactory.GetClient(AppConfig.Configuration["Rpc"]);               
                _streamingRpcClient = Solnet.Rpc.ClientFactory.GetStreamingClient(AppConfig.Configuration["StreamingRpc"], null, _serumClientWebSocket);
                _serumClient = ClientFactory.GetClient(_rpcClient, _streamingRpcClient);
                _serumClient.ConnectAsync().Wait();
                _serumMarket = _serumClient.GetMarket(AppConfig.Configuration["SerumMarketAddress"]);
                _serumPriceDecimalPlaces = ulong.Parse(AppConfig.Configuration["SerumPriceDecimalPlaces"]);                
                _serumSymbol = AppConfig.Configuration["SerumSymbol"];                

                BestPrice.Add(
                    Side.Sell, () =>
                    {
                        if (!_serumOrderBook.ContainsKey(Side.Buy) || !_serumOrderBook.ContainsKey(Side.Sell))
                        {
                            return 0;
                        }
                       
                        return _serumOrderBook[Side.Sell]
                            .OrderBy(o => o.RawPrice)
                            .First()
                            .RawPrice / ((decimal)Math.Pow(10, _serumPriceDecimalPlaces));
                    });

                BestPrice.Add(
                    Side.Buy, () =>
                    {
                        if (!_serumOrderBook.ContainsKey(Side.Buy) || !_serumOrderBook.ContainsKey(Side.Sell))
                        {
                            return 0;
                        }

                        return _serumOrderBook[Side.Buy]
                            .OrderByDescending(o => o.RawPrice)
                            .First(o => o.Owner != _mangoAccountPublicKey)
                            .RawPrice / ((decimal)Math.Pow(10, _serumPriceDecimalPlaces));
                    });
            }
            catch (Exception e)
            {
                logger.LogError(e.ToString());
                Environment.Exit(-1);
            }
        }


        public async void Initialise(IMangoClient mangoClient, MangoProgram mangoProgram, MangoGroup mangoGroup, PublicKey mangoAccountPublicKey, PublicKey solanaWalletPublicKey, Wallet mangoWallet)
        {
            _mangoClient = mangoClient;
            _mangoProgram = mangoProgram;

            _mangoGroup = mangoGroup;
            _marketIndex = _mangoGroup.GetSpotMarketIndex(_serumMarket.OwnAddress);
            _market = _serumClient.GetMarket(_mangoGroup.SpotMarkets[_marketIndex].Market);

            _mangoAccountPublicKey = mangoAccountPublicKey;
            _solanaWalletPublicKey = solanaWalletPublicKey;

            _signers.Add(mangoWallet.Account);
            _wallet = mangoWallet;

            _mangoAccount = _mangoClient.GetMangoAccount(_mangoAccountPublicKey).ParsedResult;
            _mangoAccount.LoadOpenOrdersAccounts(_rpcClient, _logger);
                        
            AccountResultWrapper<MangoAccount> mangoAccount = await _mangoClient.GetMangoAccountAsync(_mangoAccountPublicKey, Commitment.Finalized);                                   

            // ensure all open accounts are ready on boot
            while (mangoAccount.ParsedResult.SpotOpenOrders[_marketIndex].Equals(new PublicKey(new byte[32])))
            {
                // make sure open order accounts < 6
                if (mangoAccount.ParsedResult.SpotOpenOrders.Count(o => !o.Equals(new PublicKey(new byte[32]))) == 5)
                {
                    _logger.LogError("Too many open order accounts.");
                    Environment.Exit(-1);
                }

                List<TransactionInstruction> transactionInstructions = CreateSpotOpenOrders(_mangoGroup, _mangoAccountPublicKey, _mangoGroup.SpotMarkets[_marketIndex].Market);
                TransactionBuilder txBuilder = await BuildTransactionAsync(transactionInstructions);
                byte[] txBytes = txBuilder.Build(_signers);
                await _mangoClient.RpcClient.SendTransactionAsync(txBytes, true, Commitment.Processed);

                _mangoAccount = _mangoClient.GetMangoAccount(_mangoAccountPublicKey).ParsedResult;
                _mangoAccount.LoadOpenOrdersAccounts(_rpcClient, _logger);
            }            
        }

        public async Task Enqueue(Side side, decimal quantity)
        {            
            // Adds quantity to queue for hedging
            _logger.LogInformation($"Currently hedging: {UnhedgedQuantity}.");
            decimal amount = quantity * (side == Side.Buy ? 1 : -1);
            _logger.LogInformation($"Adding: {amount}.");
            UnhedgedQuantity += amount;
            _logger.LogInformation($"New hedge amount: {UnhedgedQuantity}.");
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting hedging service...");
            
            _ = Subscribe(); // Subscribe to Serum price data
            _ = Listen(); // Listen for orders to hedge
            
            _timer = new Timer(Spark, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        }

        private async Task Listen()
        {
            while (!Program.ShuttingDown)
            {
                int currentRetry = 0;
                int retryCount = 3;
                
                while (UnhedgedQuantity != 0 && !Program.FlatFreeze)
                {
                    List<TransactionInstruction> transactionInstructions = new();

                    Side orderSide = UnhedgedQuantity > 0 ? Side.Buy : Side.Sell;
                    decimal bestPrice = BestPrice[orderSide]();
                    bestPrice += Math.Round(orderSide == Side.Sell ? bestPrice * -0.0025m : bestPrice * 0.0025m, 1);

                    _logger.LogInformation($"{orderSide}ing: {Math.Abs(UnhedgedQuantity)}.");

                    #region Place Order
                    
                    Order order;
                    ulong clOrdId = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0);                                                                     

                    for (; ; )
                    {
                        _logger.LogInformation($"Placing order: " +
                                               $"symbol: {_serumSymbol}, " +
                                               $"side: {orderSide}, " +
                                               $"type: {OrderType.ImmediateOrCancel}, " +
                                               $"quantity: {Math.Abs(UnhedgedQuantity)}, " +
                                               $"price: {bestPrice}");


                        order = new OrderBuilder()
                            .SetPrice(decimal.ToSingle(bestPrice))
                            .SetQuantity(decimal.ToSingle(Math.Abs(UnhedgedQuantity)))
                            .SetSide(orderSide)
                            .SetOrderType(OrderType.ImmediateOrCancel)                            
                            .SetSelfTradeBehavior(SelfTradeBehavior.DecrementTake)
                            .SetClientOrderId(clOrdId)
                            .Build();
                        
                        transactionInstructions.Add(CreatePlaceSpotOrder2(_mangoGroup, _mangoAccount, _mangoAccountPublicKey, _market, _mangoGroup.SpotMarkets[_marketIndex].Market, order));                                              

                        ResponseValue<ErrorResult> confirmed = await SendAndConfirm(transactionInstructions);

                        if (confirmed.Value.Error == null)
                        {
                            _logger.LogInformation($"Order placed successfully. {clOrdId}.");
                            currentRetry = 0;

                            _logger.LogInformation($"Waiting 1 second after placing order...");
                            await Task.Delay(1000);

                            break;
                        }

                        _logger.LogError($"Error placing order {currentRetry}/{retryCount}. Transaction timeout.");
                        currentRetry++;

                        if (currentRetry >= retryCount)
                        {
                            _logger.LogError($"{currentRetry}/{retryCount} trues exhausted. Error placing order. Transaction timeout.");                            
                        }

                        _logger.LogInformation($"Waiting 1 second to retry placing order...");
                        await Task.Delay(1000);                        

                    }

                    #endregion

                    #region Retrieve Events

                    _logger.LogInformation($"Retrieving events for {clOrdId}.");

                    List<Event> retrievedEvents = _serumClient.GetEventQueue(_serumMarket.EventQueue, Commitment.Processed).Events.Where(e => e.ClientId == clOrdId).ToList();
                    while (!retrievedEvents.Any())
                    {
                        await Task.Delay(1000);
                        retrievedEvents = _serumClient.GetEventQueue(_serumMarket.EventQueue, Commitment.Processed).Events.Where(e => e.ClientId == clOrdId).ToList();
                    }                      

                    #endregion

                    if (retrievedEvents.Any(e => e.Flags.IsFill))
                    {
                        decimal quantityFilled;
                        var baseDecimals = _mangoGroup.Config.SpotMarkets.First(m => m.MarketIndex == _marketIndex).BaseDecimals;

                        if (orderSide == Side.Sell)
                        {
                            quantityFilled = (decimal)MarketUtils.HumanizeRawTradeQuantity((ulong)retrievedEvents.Where(e => e.Flags.IsFill).Sum(e => (decimal)e.NativeQuantityPaid), baseDecimals);
                        }
                        else
                        {
                            quantityFilled = (decimal)MarketUtils.HumanizeRawTradeQuantity((ulong)retrievedEvents.Where(e => e.Flags.IsFill).Sum(e => (decimal)e.NativeQuantityReleased), baseDecimals);
                        }
                                                                       
                        _logger.LogInformation($"Order {clOrdId} filled for {quantityFilled}.");

                        if (order.Side == Side.Buy)
                        {
                            UnhedgedQuantity -= quantityFilled;
                            HedgedPositionSize += quantityFilled;
                        }
                        else
                        {
                            UnhedgedQuantity += quantityFilled;
                            HedgedPositionSize -= quantityFilled;
                        }                        
                    }
                    else if (retrievedEvents.Any(e => e.Flags.IsOut))
                    {
                        _logger.LogInformation($"Order {clOrdId} not filled.");
                    }

                    // settle after cancel or fill
                    TransactionInstruction settleFunds = CreateSettleFunds(_mangoGroup, _mangoAccount, _mangoAccountPublicKey, _market, _mangoGroup.SpotMarkets[_marketIndex].Market);
                    await SendAndConfirm(new List<TransactionInstruction>() { settleFunds });                    

                    _logger.LogInformation($"Remaining quantity: {UnhedgedQuantity}.");
                }

                await Task.Delay(100); // Breather
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        { }

        private async void Spark(object state)
        { }

        private async Task Subscribe()
        {
            bool connectedAndSubscribed = false;

            while (!connectedAndSubscribed)
            {
                try
                {
                    if (_serumClientWebSocket.State == WebSocketState.Aborted || _serumClientWebSocket.State == WebSocketState.Closed)
                    {
                        _serumClient.StreamingRpcClient.ConnectionStateChangedEvent -= StreamingRpcClientOnConnectionStateChangedEvent;
                        _logger.LogInformation($"WebSocket state: {_serumClientWebSocket.State}, reconnecting.");
                        _serumClientWebSocket.Dispose();
                        _serumClientWebSocket = new();
                        _streamingRpcClient = Solnet.Rpc.ClientFactory.GetStreamingClient(AppConfig.Configuration["StreamingRpc"], null, _serumClientWebSocket);
                        _serumClient = ClientFactory.GetClient(_rpcClient, _streamingRpcClient);
                    }
                    else
                    {
                        await _serumClient.StreamingRpcClient.ConnectAsync();
                    }

                    if (_serumClientWebSocket.State == WebSocketState.Open)
                    {
                        _serumClient.StreamingRpcClient.ConnectionStateChangedEvent += StreamingRpcClientOnConnectionStateChangedEvent;

                        async Task SubscribeToBids()
                        {
                            _serumSubscriptions.Add(await _serumClient.SubscribeOrderBookSideAsync(async (_, side, _) =>
                            {
                                _serumOrderBook[Side.Buy] = side.GetOrders();
                                await PriceUpdate.Invoke(null, EventArgs.Empty);

                            }, _serumMarket.Bids, Commitment.Processed));
                        }

                        async Task SubscribeToAsks()
                        {
                            _serumSubscriptions.Add(await _serumClient.SubscribeOrderBookSideAsync(async (_, side, _) =>
                            {
                                _serumOrderBook[Side.Sell] = side.GetOrders();
                                await PriceUpdate.Invoke(null, EventArgs.Empty);

                            }, _serumMarket.Asks, Commitment.Processed));
                        }                        

                        // Subscribe to bids and asks at the same time
                        _logger.LogInformation("Connecting to Serum order book data feed...");
                        await Task.WhenAll(new List<Task> { SubscribeToBids(), SubscribeToAsks() });                        
                        _logger.LogInformation("Connected to Serum order book data feed.");
                        connectedAndSubscribed = true;
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError("Error subscribing to Serum data feeds. " + e.Message);
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
                await Subscribe();
            }
        }        

        public async Task<TransactionBuilder> BuildTransactionAsync(List<TransactionInstruction> instructions)
        {
            // Lock while getting blockhash from memory or blockchain
            while (_gettingBlockHash)
            {
                await Task.Delay(100);
            }

            // Try get the blockhash from memory, if it has expired, get another from the blockchain
            if (!_cache.TryGetValue("blockhash", out string blockHash))
            {
                _gettingBlockHash = true;

                for (; ; )
                {
                    var request = await _rpcClient.GetLatestBlockHashAsync();
                    if (!request.WasSuccessful)
                    {
                        _logger.LogError($"Error getting blockhash: {request.Reason}");
                        await Task.Delay(1000);
                    }
                    else
                    {
                        blockHash = request.Result.Value.Blockhash;
                        _cache.Set("blockhash", blockHash, new TimeSpan(0, 0, 0, 15));
                        _gettingBlockHash = false;
                        break;
                    }
                }
            }

            TransactionBuilder txBuilder = new TransactionBuilder()
                .SetFeePayer(_solanaWalletPublicKey)
                .SetRecentBlockHash(blockHash);

            foreach (TransactionInstruction transactionInstruction in instructions)
                txBuilder.AddInstruction(transactionInstruction);

            return txBuilder;
        }

        private TransactionInstruction CreatePlaceSpotOrder2(MangoGroup mangoGroup, MangoAccount mangoAccount, PublicKey mangoAccountAddress, Market market, PublicKey spotMarket, Order order)
        {            
            var baseTokenInfo = mangoGroup.Tokens.FirstOrDefault(x => x.Mint.Equals(market.BaseMint));
            if (baseTokenInfo == null) return null;
            int baseTokenIndex = mangoGroup.GetTokenIndex(baseTokenInfo.Mint);
            int baseRootBankIndex = mangoGroup.GetRootBankIndex(baseTokenInfo.RootBank);
            RootBank baseRootBank = mangoGroup.RootBankAccounts[baseRootBankIndex];
            PublicKey baseNodeBankKey = baseRootBank.NodeBanks.FirstOrDefault(x => x.Key != SystemProgram.ProgramIdKey.Key);
            if (baseNodeBankKey == null) return null;
            int baseNodeBankIndex = baseRootBank.GetNodeBankIndex(baseNodeBankKey);
            NodeBank baseNodeBank = baseRootBank.NodeBankAccounts[baseNodeBankIndex];

            var quoteTokenInfo = mangoGroup.Tokens.FirstOrDefault(x => x.Mint.Equals(market.QuoteMint));
            if (quoteTokenInfo == null) return null;
            int quoteRootBankIndex = mangoGroup.GetRootBankIndex(quoteTokenInfo.RootBank);
            RootBank quoteRootBank = mangoGroup.RootBankAccounts[quoteRootBankIndex];
            PublicKey quoteNodeBankKey = quoteRootBank.NodeBanks.FirstOrDefault(x => x.Key != SystemProgram.ProgramIdKey.Key);
            if (quoteNodeBankKey == null) return null;
            int quoteNodeBankIndex = quoteRootBank.GetNodeBankIndex(quoteNodeBankKey);
            NodeBank quoteNodeBank = quoteRootBank.NodeBankAccounts[quoteNodeBankIndex];

            order.ConvertOrderValues(baseTokenInfo.Decimals, quoteTokenInfo.Decimals, market);

            PublicKey dexSigner = SerumProgramData.DeriveVaultSignerAddress(market, _serumProgram.ProgramIdKey);
            List<PublicKey> openOrders = mangoAccount.SpotOpenOrders.Where((t, i) => t != null && mangoAccount.InMarginBasket[i]).ToList();
            if (openOrders.Count == 0) openOrders.Add(mangoAccount.SpotOpenOrders[baseTokenIndex]);

            return _mangoProgram.PlaceSpotOrder2(
                    Constants.MangoGroup,
                    mangoAccountAddress,
                    _wallet.Account,
                    mangoGroup.MangoCache,
                    spotMarket,
                    market.Bids,
                    market.Asks,
                    market.RequestQueue,
                    market.EventQueue,
                    market.BaseVault,
                    market.QuoteVault,
                    baseTokenInfo.RootBank,
                    baseNodeBankKey,
                    baseNodeBank.Vault,
                    quoteTokenInfo.RootBank,
                    quoteNodeBankKey,
                    quoteNodeBank.Vault,
                    mangoGroup.SignerKey,
                    dexSigner,
                    mangoGroup.SerumVault,
                    mangoAccount.SpotOpenOrders,
                    baseTokenIndex,
                    order
                );
        }

        private TransactionInstruction CreateSettleFunds(MangoGroup mangoGroup, MangoAccount mangoAccount, PublicKey mangoAccountAddress, Market market, PublicKey spotMarket)
        {
            var baseTokenInfo = mangoGroup.Tokens.FirstOrDefault(x => x.Mint.Equals(market.BaseMint));
            if (baseTokenInfo == null) return null;
            int baseTokenIndex = mangoGroup.GetTokenIndex(baseTokenInfo.Mint);
            int baseRootBankIndex = mangoGroup.GetRootBankIndex(baseTokenInfo.RootBank);
            RootBank baseRootBank = mangoGroup.RootBankAccounts[baseRootBankIndex];
            PublicKey baseNodeBankKey = baseRootBank.NodeBanks.FirstOrDefault(x => x.Key != SystemProgram.ProgramIdKey.Key);
            if (baseNodeBankKey == null) return null;
            int baseNodeBankIndex = baseRootBank.GetNodeBankIndex(baseNodeBankKey);
            NodeBank baseNodeBank = baseRootBank.NodeBankAccounts[baseNodeBankIndex];

            var quoteTokenInfo = mangoGroup.Tokens.FirstOrDefault(x => x.Mint.Equals(market.QuoteMint));
            if (quoteTokenInfo == null) return null;
            int quoteRootBankIndex = mangoGroup.GetRootBankIndex(quoteTokenInfo.RootBank);
            RootBank quoteRootBank = mangoGroup.RootBankAccounts[quoteRootBankIndex];
            PublicKey quoteNodeBankKey = quoteRootBank.NodeBanks.FirstOrDefault(x => x.Key != SystemProgram.ProgramIdKey.Key);
            if (quoteNodeBankKey == null) return null;
            int quoteNodeBankIndex = quoteRootBank.GetNodeBankIndex(quoteNodeBankKey);
            NodeBank quoteNodeBank = quoteRootBank.NodeBankAccounts[quoteNodeBankIndex];

            PublicKey dexSigner = SerumProgramData.DeriveVaultSignerAddress(market, _serumProgram.ProgramIdKey);

            return _mangoProgram.SettleFunds(
                    Constants.MangoGroup,
                    mangoGroup.MangoCache,
                    mangoAccountAddress,
                    _wallet.Account,
                    spotMarket,
                    mangoAccount.SpotOpenOrders[baseTokenIndex],
                    mangoGroup.SignerKey,
                    market.BaseVault,
                    market.QuoteVault,
                    baseTokenInfo.RootBank,
                    baseNodeBankKey,
                    quoteTokenInfo.RootBank,
                    quoteNodeBankKey,
                    baseNodeBank.Vault,
                    quoteNodeBank.Vault,
                    dexSigner);
        }

        private TransactionInstruction CreateCancelSpotOrderByOrderId(MangoGroup mangoGroup, MangoAccount mangoAccount, PublicKey mangoAccountAddress, Market market, PublicKey spotMarket, BigInteger orderId, Side side)
        {
            var baseTokenInfo = mangoGroup.Tokens.FirstOrDefault(x => x.Mint.Equals(market.BaseMint));
            if (baseTokenInfo == null) return null;
            int baseTokenIndex = mangoGroup.GetTokenIndex(baseTokenInfo.Mint);

            return _mangoProgram.CancelSpotOrder(
                    Constants.MangoGroup,
                    _wallet.Account,
                    mangoAccountAddress,
                    spotMarket,
                    market.Bids,
                    market.Asks,
                    mangoAccount.SpotOpenOrders[baseTokenIndex],
                    mangoGroup.SignerKey,
                    market.EventQueue,
                    orderId,
                    side
                    );
        }

        private List<TransactionInstruction> CreateSpotOpenOrders(MangoGroup mangoGroup, PublicKey mangoAccount, PublicKey spotMarket)
        {
            var minBalance = _rpcClient.GetMinimumBalanceForRentExemption(OpenOrdersAccount.Layout.SpanLength);

            Account acc = new Account();
            _signers.Add(acc);

            return new List<TransactionInstruction>()
            {
                SystemProgram.CreateAccount(
                    _wallet.Account,
                    acc,
                    minBalance.Result,
                    OpenOrdersAccount.Layout.SpanLength,
                    _serumProgram.ProgramIdKey),

                _mangoProgram.InitSpotOpenOrders(
                    Constants.MangoGroup,
                    mangoAccount,
                    _wallet.Account,
                    acc,
                    spotMarket,
                    mangoGroup.SignerKey)
            };       
        }

        public async Task<ResponseValue<ErrorResult>> ConfirmTransaction(IStreamingRpcClient streamingRpcClient, string hash, Commitment commitment = Commitment.Finalized)
        {
            TaskCompletionSource t = new();
            ResponseValue<ErrorResult> result = null;

            var s = await streamingRpcClient.SubscribeSignatureAsync(hash, (s, e) =>
            {
                result = e;
                t.SetResult();
            }, commitment);

            var timeout = commitment == Commitment.Finalized ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(30);
            var delay = Task.Delay(timeout);

            Task.WaitAny(t.Task, delay);

            if (!t.Task.IsCompleted)
            {
                await s.UnsubscribeAsync();
            }

            if (delay.Status == TaskStatus.RanToCompletion)
            {
                result.Value.Error = new TransactionError();
            }

            return result;
        }

        private async Task<ResponseValue<ErrorResult>> SendAndConfirm(List<TransactionInstruction> transactionInstructions)
        {
            TransactionBuilder txBuilder = await BuildTransactionAsync(transactionInstructions);
            byte[] txBytes = txBuilder.Build(_signers);
            string transactionHash = Encoders.Base58.EncodeData(txBytes.Skip(1).Take(64).ToArray());
            await _mangoClient.RpcClient.SendTransactionAsync(txBytes, true, Commitment.Processed);
            return await ConfirmTransaction(_streamingRpcClient, transactionHash, Commitment.Processed);
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}