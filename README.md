# Mango Maker
A **work in progress** backstop trading bot built for Mango Markets. Trading on Mango Markets' perps and hedging on Mango Markets' spot (Serum).

**Features**
- Built using Solnet.Mango - https://github.com/bmresearch/Solnet.Mango/
- Trades Mango Perps, hedges on Mango Spot (Serum).
- Connects directly to the Solana blockchain by interfacing with RPC nodes.
- RPC WebSocket connection for Serum spot prices.
- Mango fills feed WebSocket for Mango perps.
- Basic web server for viewing status and log.

**Installation**
1. Install Visual Studio 2019/2022/For Mac (or your .NET preferred IDE)
2. Install .NET 5
3. Clone/download the repo
4. Restore Nuget packages
5. Set parameters in appsettings.json
6. Build and run
7. Press F in the console to begin quoting
8. Press S in the console to cancel all open orders and shut down

**App Settings**

Parameters set manually in the appsettings.json file:
```
"ApiPort": The port at which you can access the web server, e.g. 8080 = http://192.168.0.1:8080
"HedgeTrade": Set this to True if you want to hedge your Perp positions on Spot.
"Rpc": The Solana RPC server or cluster you wish to use. By default I've included the same token as used in the mango-client-v3. Contact Mango Markets in discord for a token with higher rate limits.
"StreamingRpc": The Solana RPC server or cluster you wish to make WebSocket connections to.
"FillFeedUrl": The Mango Markets custom perps fill feed URL. See: https://docs.mango.markets/api-and-websocket/fills-websocket-feed
"Keystore": The path and filename of your .json keystore associated with your Solana wallet.
"SolanaWalletPublicKey": Your Solana wallet address.
"MangoAccountPublicKey": The public key of your Mango Markets trading account, found on the accounts page - https://trade.mango.markets/account
"PerpMarketPublicKey": The public key of the Mango Markets perp market you want to trade.
"PerpMarketOracle": The public key of the oracle used to track the market price of the Mango Markets perp contract you are trading.
"Symbol": The ticker/symbol/contract name of the Mango Markets perp contract you want to trade. e.g. "SOL-PERP".
"SerumSymbol": The ticker/symbol name of the Mango Markets spot (Serum) contract you want to trade. e.g. "SOL/USDC".
"OrderSize": The quantity to trade as seen on the UI. e.g. 43.10
"MaximumPositionSize": The maximum position size you will allow for in 1 direction (Long or Short). The bot will only quote to decrease your position after your position reaches this size.
"SerumMarketAddress": The public key of the Serum market you want to hedge on.
"SpreadBps": The spread in basis points from the mid price that you want to quote at on both sides of the book.
"MangoPriceDecimalPlaces": The price decimal places on the Mango UI of the perp contract you want to trade.
"SerumQuantityDecimalPlaces": The quantity decimal places on the Mango UI of the Serum market you want to hedge on.
"SerumPriceDecimalPlaces": The price decimal places on the Mango UI of the Serum market you want to hedge on.
```

**TODO:**
- [ ] Upgrade to .NET 6.
- [ ] Determine price and quantity decimal places automatically.
- [ ] Determine Perp Market public key from symbol name.
- [ ] Determine Serum Market public key from symbol name. 
- [ ] Open a new fills WebSocket feed before closing the old idle one.
- [ ] Determine the perp oracle public key automatically.
- [ ] Determine first Mango account from wallet address.
- [ ] Subscribe to Serum events queue instead of retrieving.
- [ ] Migrate to Solnet.Mango's implementation of the perp custom fill feed.
- [ ] Use new Solnet.Serum.MarketUtils for converting between raw and UI price and quantity.
