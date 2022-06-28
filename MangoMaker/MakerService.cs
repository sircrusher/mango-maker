using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Solnet.KeyStore;
using Solnet.Mango;
using Solnet.Mango.Models;
using Solnet.Mango.Types;
using Solnet.Programs.Models;
using Solnet.Rpc.Builders;
using Solnet.Rpc.Core.Http;
using Solnet.Rpc.Models;
using Solnet.Rpc.Types;
using Solnet.Serum.Models;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;
using ClientFactory = Solnet.Mango.ClientFactory;
using Constants = Solnet.Mango.Models.Constants;

namespace MangoMaker
{
    public partial class MakerService : IHostedService, IDisposable
    {
        private readonly HedgeService _hedgeService;        
        private Timer _timer;
        private readonly ILogger<MakerService> _logger;
        private readonly IMemoryCache _cache;
        private readonly DateTime _startTime = DateTime.UtcNow;
        private readonly ConcurrentQueue<string> _transactionLog = new();
        
        private readonly string _symbol;
        private readonly decimal _spreadBps;
        private readonly bool _hedgeTrades;
        private readonly long _maximumPositionSize;
        private readonly string _fillFeedUrl;
        private readonly int _cexQuantityDecimalPlaces;

        public MakerService(ILogger<MakerService> logger, IMemoryCache cache, HedgeService hedgeService)
        {
            try
            {
                _logger = logger;
                _hedgeService = hedgeService;
                _hedgeService.PriceUpdate += (_, _) => UnhedgeMango();
                _cache = cache;

                _cexQuantityDecimalPlaces = int.Parse(AppConfig.Configuration["SerumQuantityDecimalPlaces"]);
                _fillFeedUrl = AppConfig.Configuration["FillFeedUrl"];
                _hedgeTrades = bool.Parse(AppConfig.Configuration["HedgeTrade"]);
                _spreadBps = decimal.Divide(int.Parse(AppConfig.Configuration["SpreadBps"]), 10000);
                _symbol = AppConfig.Configuration["Symbol"];                
                _mangoProgram = MangoProgram.CreateMainNet();
                _mangoStreamingClientUrl = AppConfig.Configuration["StreamingRpc"];
                _solanaWalletPublicKey = new PublicKey(AppConfig.Configuration["SolanaWalletPublicKey"]);
                _mangoAccountPublicKey = new PublicKey(AppConfig.Configuration["MangoAccountPublicKey"]);
                _mangoWallet = new SolanaKeyStoreService().RestoreKeystoreFromFile(AppConfig.Configuration["KeyStore"]);
                _mangoPriceDecimalPlaces = int.Parse(AppConfig.Configuration["MangoPriceDecimalPlaces"]);
                _rpcClient = Solnet.Rpc.ClientFactory.GetClient(AppConfig.Configuration["Rpc"], null);
                _streamingRpcClient = Solnet.Rpc.ClientFactory.GetStreamingClient(AppConfig.Configuration["StreamingRpc"], null, _mangoClientWebSocket);
                _mangoClient = ClientFactory.GetClient(_rpcClient, _streamingRpcClient);
                _perpMarket = GetSingleAccount(_mangoClient.GetPerpMarketAsync(AppConfig.Configuration["PerpMarketPublicKey"]), _logger);
                _mangoGroup = GetSingleAccount(_mangoClient.GetMangoGroupAsync(Constants.MangoGroup, Commitment.Processed), _logger);
                _marketIndex = _mangoGroup.ParsedResult.GetPerpMarketIndex(new PublicKey(AppConfig.Configuration["PerpMarketPublicKey"]));
                _token = _mangoGroup.ParsedResult.Tokens[_marketIndex];
                _quoteToken = _mangoGroup.ParsedResult.GetQuoteTokenInfo();

                (_, _maximumPositionSize) = _perpMarket.ParsedResult.UiToNativePriceQuantity(0, double.Parse(AppConfig.Configuration["MaximumPositionSize"]), _token.Decimals, _quoteToken.Decimals);
                (_, _mangoOrderSize) = _perpMarket.ParsedResult.UiToNativePriceQuantity(0, double.Parse(AppConfig.Configuration["OrderSize"]), _token.Decimals, _quoteToken.Decimals);                               

                AccountResultWrapper<MangoAccount> mangoAccount = GetSingleAccount(_mangoClient.GetMangoAccountAsync(_mangoAccountPublicKey, Commitment.Processed), _logger);
                if (mangoAccount != null)
                {
                    _mangoSpotOrders = mangoAccount.ParsedResult.SpotOpenOrders;
                    _mangoGroup.ParsedResult.LoadRootBanks(_mangoClient);
                }
                else
                {
                    throw new Exception("Error getting Mango account.");
                }

                _hedgeService.Initialise(_mangoClient, _mangoProgram, _mangoGroup.ParsedResult, _mangoAccountPublicKey, _solanaWalletPublicKey, _mangoWallet);
            }
            catch (Exception e)
            {
                logger.LogError($"{e}");
                Environment.Exit(-1);
            }
        }
        
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            TimeSpan interval = TimeSpan.FromSeconds(1);
            DateTime nextRunTime = DateTime.UtcNow.AddSeconds(1 - DateTime.UtcNow.Second % 1);
            TimeSpan firstInterval = nextRunTime.Subtract(DateTime.UtcNow);

            void Action()
            {
                Task t1 = Task.Delay(firstInterval, cancellationToken);
                t1.Wait(cancellationToken);
                _timer = new Timer(Spark, null, TimeSpan.Zero, interval);
            }

            _logger.LogInformation("Starting market making service...");

            await Task.Run(Action, cancellationToken);

            // Get open position
            _mangoPositionSize = await GetMangoPosition();

            // Connect WebSocket subscriptions
            _ = ConnectMangoFillFeed();
            await MangoSubscribe();
        }

        public async void Spark(object state)
        {
            if (Program.ShuttingDown)
                return;

            if (Console.KeyAvailable)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                switch (key.Key)
                {
                    case ConsoleKey.S:
                        await Shutdown();
                        break;

                    case ConsoleKey.F:
                        _logger.LogInformation(Program.FlatFreeze ? "Unfreezing..." : "Freezing...");
                        Program.FlatFreeze = !Program.FlatFreeze;
                        break;

                    default:
                        _logger.LogInformation("Nope.");
                        break;
                }
            }
        }

        public Task StopAsync(CancellationToken stoppingToken)
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }

        public async Task Shutdown()
        {
            if (!Program.ShuttingDown)
            {
                Program.ShuttingDown = true;
                _logger.LogInformation("Shutting down...");

                foreach (Subscription subscription in _mangoSubscriptions)
                {
                    await _mangoClient.StreamingRpcClient.UnsubscribeAsync(subscription.SubscriptionState);
                }

                await Task.WhenAll(new List<Task> { CancelMangoOrders(true) });
                Environment.Exit(0);
            }
        }        

        private async Task PlaceMangoOrders()
        {
            if (!Program.ShuttingDown && !Program.FlatFreeze)
            {
                List<string> log = new List<string>();

                for (int i = 0; i < 5; i++)
                {
                    
                    List<TransactionInstruction> instructions = new List<TransactionInstruction>
                    {
                        // Cancel all previous orders
                        _mangoProgram.CancelAllPerpOrders(
                            Constants.MangoGroup,
                            _mangoAccountPublicKey,
                            _solanaWalletPublicKey,
                            _mangoGroup.ParsedResult.PerpetualMarkets[_marketIndex].Market,
                            _perpMarket.ParsedResult.Bids,
                            _perpMarket.ParsedResult.Asks,
                            20),

                        // Redeem mango tokens
                        _mangoProgram.RedeemMango(
                            Constants.MangoGroup,
                            Constants.MangoCache,
                            _mangoAccountPublicKey,
                            _solanaWalletPublicKey,
                            _mangoGroup.ParsedResult.PerpetualMarkets[_marketIndex].Market,
                            _perpMarket.ParsedResult.MangoVault,
                            _mangoGroup.ParsedResult.Tokens[0].RootBank,
                            _mangoGroup.ParsedResult.RootBankAccounts[0].NodeBanks[0],
                            _mangoGroup.ParsedResult.RootBankAccounts[0].NodeBankAccounts[0].Vault,
                            _mangoGroup.ParsedResult.SignerKey)
                    };

                    // If not currently trying to hedge a position
                    if (_hedgeService.UnhedgedQuantity == 0)
                    {
                        if (_mangoReduceOrder != null)
                        {
                            // Add reduce order for open position
                            instructions
                                .Add(_mangoProgram.PlacePerpOrder(
                                    Constants.MangoGroup,
                                    _mangoAccountPublicKey,
                                    _solanaWalletPublicKey,
                                    Constants.MangoCache,
                                    _mangoGroup.ParsedResult.PerpetualMarkets[_marketIndex].Market,
                                    _perpMarket.ParsedResult.Bids,
                                    _perpMarket.ParsedResult.Asks,
                                    _perpMarket.ParsedResult.EventQueue,
                                    _mangoSpotOrders,
                                    _mangoReduceOrder.Side,
                                    PerpOrderType.PostOnly,
                                    _mangoReduceOrder.RawPrice,
                                    _mangoReduceOrder.RawQuantity,
                                    0ul,
                                    true
                                ));

                            decimal price = _perpMarket.ParsedResult.PriceLotsToNumber(new I80F48(_mangoReduceOrder.RawPrice), _token.Decimals, _quoteToken.Decimals);
                            decimal quantity = _perpMarket.ParsedResult.BaseLotsToNumber(_mangoReduceOrder.RawQuantity, _token.Decimals);

                            log.Add($"Reduce: {_mangoReduceOrder.Side} {quantity} {_symbol} @ ${price}. ");
                        }

                        foreach (Side side in Enum.GetValues(typeof(Side)))
                        {
                            // Add updated order
                            if (_mangoOrders.ContainsKey(side) && _mangoOrders[side] != null && _hedgeService.UnhedgedQuantity == 0)
                            {
                                // Add order instruction for each side
                                instructions
                                    .Add(_mangoProgram.PlacePerpOrder(
                                        Constants.MangoGroup,
                                        _mangoAccountPublicKey,
                                        _solanaWalletPublicKey,
                                        Constants.MangoCache,
                                        _mangoGroup.ParsedResult.PerpetualMarkets[_marketIndex].Market,
                                        _perpMarket.ParsedResult.Bids,
                                        _perpMarket.ParsedResult.Asks,
                                        _perpMarket.ParsedResult.EventQueue,
                                        _mangoSpotOrders,
                                        side,
                                        PerpOrderType.PostOnly,
                                        _mangoOrders[side].RawPrice,
                                        _mangoOrders[side].RawQuantity,
                                        1ul, //reduce order
                                        _mangoOrders[side].ReduceOnly
                                    ));

                                decimal price = _perpMarket.ParsedResult.PriceLotsToNumber(new I80F48(_mangoOrders[side].RawPrice), _token.Decimals, _quoteToken.Decimals);
                                decimal quantity = _perpMarket.ParsedResult.BaseLotsToNumber(_mangoOrders[side].RawQuantity, _token.Decimals);

                                log.Add($"{_mangoOrders[side].Side} {quantity} {_symbol} @ ${price}. ");
                            }
                        }
                    }

                    TransactionBuilder txBuilder = await BuildTransactionAsync(instructions);
                    byte[] txBytes = txBuilder.Build(new List<Account>() { _mangoWallet.Account });
                    string transactionHash = Encoders.Base58.EncodeData(txBytes.Skip(1).Take(64).ToArray());

                    if (_transactionLog.Count(x => string.Equals(x, transactionHash)) < 2)
                    {
                        _transactionLog.Enqueue(JsonConvert.SerializeObject(instructions));
                        
                        RequestResult<string> result = await _mangoClient.RpcClient.SendTransactionAsync(txBytes, true, Commitment.Processed);

                        if (!result.WasSuccessful)
                        {
                            _logger.LogError($"Error placing order on Mango. {result.ServerErrorCode}: {result.Reason}");
                        }
                        else
                        {
                            if (_transactionLog.Count > 60)
                            {
                                _transactionLog.TryDequeue(out _);
                            }

                            if (!_transactionLog.TakeLast(2).AreAllSame())
                            {
                                StringBuilder logMessage = new();
                                log.ForEach(l => logMessage.Append(l));
                                _logger.LogInformation($"Tx: {logMessage}{transactionHash}");
                            }

                            return;
                        }
                    }
                    else 
                    {
                        // transaction was already sent.
                        return;
                    }
                }
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

        public decimal TruncateDecimal(decimal value, int precision)
        {
            decimal step = (decimal)Math.Pow(10, precision);
            decimal tmp = Math.Truncate(step * value);
            return tmp / step;
        }
    }
}