# BTCNutServer (Cashu) - BTCPay Server Plugin

## ⚠️ Disclaimer
This plugin is in early beta – use with caution. The author is NOT a cryptographer and this work has not been reviewed. This means that there is very likely a fatal flaw somewhere. Cashu is still experimental and not production-ready.

## Overview

BTCNutServer enables BTCPay Server to accept Bitcoin payments via **Cashu tokens**, offering two flexible payment models:

### 1. Trusted Mints (Swap)
- Swap happens inside the mint: you swap your Cashu tokens to get a new token to your wallet which you can export.
- The merchant accepts Cashu tokens and swaps them to prevent double-spending (per [NUT-03](https://github.com/cashubtc/nuts/blob/main/03.md)).
- Requires trust in the mint.
- Merchant can check their balance in a Cashu wallet, export it as a serialized token, and transfer or melt it (e.g., at [redeem.cashu.me](https://redeem.cashu.me)).
- Store doesn't have to have lightning backend connected.

### 2. Melt Immediately
- Melt means redeeming tokens directly to a Lightning wallet, receiving sats instantly.
- Some mints return keyset fees, but not all do ([NUT-05](https://github.com/cashubtc/nuts/blob/main/05.md), [NUT-08](https://github.com/cashubtc/nuts/blob/main/08.md)).
- Tokens coming from trusted mints will be received and held similarly to the first model.
- The store must have a Lightning backend connected.

> 💡 Both models are unit-agnostic — token value is always validated against its satoshi worth on the mint.

---

## 🧾 Checkout Flow

Customers can pay with Cashu in two ways (both handled via HTTP POST request):

1. **QR Code** – Customer scans the payment request QR code using their Cashu wallet.  
   ⚠️ Works only on a deployed server. Localhost environments are not supported since it's not possible to HTTP POST them.
2. **Paste Token** – Customer pastes the Cashu token directly into the payment field.
   ![Checkout](./Screenshots/Checkout.png)

---

## ⚙️ Configuration

You can configure the plugin to fit your needs. Main options include:
![Configuration](./Screenshots/Configuration.png)
### 💡 Mode
Melt Immediately
If your Lightning node is connected, payments from trusted mints will be automatically swapped to sats. Tokens from untrusted mints will be melted normally.

Swap All
Only tokens from trusted mints are accepted. These are always swapped to Lightning directly — everything else gets rejected.
![Payment Models](./Screenshots/PaymentModels.png)

### 💰 Max Payment Amount
Maximum accepted payment amount (in sats). Cashu will be unavailable as a payment method for invoices with too large amount.

### ⚡ Max Lightning Fee
Max Lightning fee you're willing to pay, expressed as a percentage of the transaction amount. Advised to use at least 2%

### 🧊 Max Mint Fee
Max mint melt fee you're okay with, also expressed as a percentage of the transaction amount. Advised to use at least 1%.

### 🪙 Customer Fee Advance
Fixed amount (in sats) that the customer pays upfront to cover fees.
This is useful for small payments — the fee percentages above don’t include what's already covered by this advance.

### 🏦 Trusted Mints
A list of trusted mints (one per line).
Format: full URL without a trailing slash (e.g. https://mint.example.com)
Tokens from these mints are eligible for automatic swapping.

---
## Fallback Mechanism
Payments can fail. For example, this can happen if the server loses connection with the mint while long-polling the mint's API. In this scenario, if the customer sees their token as 'Spent', they should contact the merchant and provide the Invoice ID.
Merchant will enter Cashu Configuration Screen, then select Show Interrupted Payments button. Merchant will see the list of interrupted payments and can poll them, by single click each. If the payment went through, it will be automatically marked as settled.
## ✨ Features

- **Privacy-Focused** – Accept Bitcoin payments without exposing transaction details.
- **Offline Transactions** – Enables payments without the need for real-time internet access on customer side. Perfect for POS.
- **Seamless Integration** – Works directly within BTCPay Server.
- **No Trusted Third Party (Optional)** – With melt mode, merchants receive sats directly, trustless.
- **Simple Wallet Embedded**
  ![Simple Wallet](./Screenshots/Wallet.png)


---

## Contributing

- Fork the repo on GitHub
- Clone the project to your own machine
- Commit changes to your own branch
- Push your work back up to your fork
- Submit a Pull request for review

---

## 🧷 Final Notes

This plugin is still evolving alongside the Cashu ecosystem. Use it at your own discretion and test thoroughly before production deployment.  
The author is not responsible for potential losses or issues resulting from use. Feedback and contributions are welcome!

🥜󠅓󠅑󠅣󠅘󠅥󠄲󠅟󠄢󠄶󠅤󠅔󠅝󠅘󠄠󠅔󠄸󠄲󠅪󠄿󠅙󠄨󠅦󠅒󠅇󠅜󠅥󠅔󠄳󠄥󠅚󠅒󠄢󠅜󠅥󠅒󠄣󠄽󠅥󠅑󠅇󠄩󠅘󠅔󠅇󠄾󠅪󠅉󠅈󠅂󠅘󠅔󠄹󠄷󠅙󠅉󠅇󠅜󠄹󠄱󠄵󠄩󠄦󠄣󠅩󠅟󠄵󠄾󠅇󠅨󠅘󠅓󠄹󠅕󠅛󠅉󠅇󠄵󠄲󠅉󠅈󠄾󠄤󠅁󠄴󠅅󠄥󠅊󠄷󠅁󠄢󠅊󠄴󠅅󠄤󠄽󠅄󠅆󠅚󠅉󠅇󠄽󠅪󠄿󠅄󠅉󠄡󠄽󠅚󠄽󠄢󠅉󠅄󠅛󠅪󠅉󠅪󠄱󠅨󠅉󠅪󠅉󠄥󠅉󠅚󠅁󠄢󠅉󠄢󠄽󠅩󠄾󠅄󠅊󠅘󠄽󠄢󠄹󠄠󠅊󠄷󠄽󠄣󠄽󠅪󠅛󠄡󠄾󠅝󠅂󠅚󠅊󠄴󠄵󠄡󠅉󠅄󠅗󠅪󠅊󠅄󠅁󠄡󠄾󠅇󠅊󠅚󠅉󠄢󠄾󠅘󠅉󠄡󠅗󠅘󠄱󠅝󠄻󠅪󠅛󠄤󠅒󠅜󠅁󠅅󠄝󠅀󠅚󠄥󠄳󠄝󠅉󠄿󠄿󠄿󠅗󠅕󠅗󠄩󠄦󠄸󠅑󠅔󠄾󠅖󠅧󠅅󠄠󠅦󠅄󠅗󠄧󠄤󠄶󠅔󠄡󠄩󠅇󠅨󠅉󠅇󠅃󠅚󠅉󠅇󠅆󠅉󠄹󠄸󠄠󠄼󠄲󠅒󠅓󠄠󠅞󠅞󠅜󠅘󠅩󠅕󠄺󠅙󠄣󠅠󠅜󠅂󠄣󠅆󠅇󠄳󠄦󠄩󠅣󠅛󠅟󠅓󠅘󠅤󠄳󠅝󠅁󠄼󠅄󠅙󠄶󠄵󠅢󠄶󠄦󠅡󠅉󠅈󠄾󠅉󠄹󠄶󠅒󠄥󠅕󠄢󠅚󠄶󠅒󠄾󠅕󠄡󠄡󠅉󠅞󠄽󠅃󠄱󠅔󠅀󠅄󠅓󠄦󠅝󠅟󠅛󠄝󠅥󠅖󠄧󠄵󠄿󠅣󠄺󠅛󠅛󠅄󠄶󠅥󠅂󠅓󠅂󠄶󠅃󠅉󠅈󠄺󠅉󠄹󠄱󠅜󠅪󠅢󠄠󠅛󠄴󠅤󠅕󠅅󠅣󠅏󠅆󠅖󠄷󠅢󠄳󠅁󠄣󠄷󠅄󠄳󠄽󠅑󠅨󠄶󠄴󠅒󠅂󠄧󠅅󠅁󠅑󠅝󠅅󠅔󠅂󠅢󠄡󠅧󠄺󠄨󠅪󠅠󠄷󠄶󠅘󠄱󠅝󠄶󠅪󠅕󠄵󠄱󠄢󠅊󠅝󠅁󠅨󠄽󠅚󠄲󠅘󠄾󠅄󠅅󠅧󠅉󠄢󠅁󠄢󠅉󠅄󠄲󠅛󠄾󠅇󠄹󠄡󠄽󠅄󠅛󠄤󠅉󠅪󠅛󠅩󠅊󠅚󠅅󠄡󠅉󠅇󠅉󠄢󠄿󠅄󠄶󠅝󠅉󠅄󠄱󠅨󠄾󠅇󠅊󠅙󠄿󠄷󠅁󠄤󠄿󠄴󠄱󠄥󠄽󠅄󠄵󠄣󠄽󠅇󠅉󠅩󠅉󠅚󠄽󠅧󠄾󠄷󠄹󠄣󠄽󠄢󠅅󠅪󠅉󠅚󠅓󠅧󠅉󠅇󠄾󠅉󠄹󠅁󠅀󠅄󠅒󠅝󠅪󠅓󠄝󠅄󠅢󠄼󠄲󠄼󠄤󠅡󠄳󠅝󠅏󠄿󠄦󠅆󠄦󠅆󠅡󠅡󠄣󠄩󠄤󠅨󠄲󠅑󠄤󠅦󠄧󠅂󠅇󠅈󠅃󠅠󠅜󠄤󠄽󠅄󠅉󠄷󠄶󠅛󠅟󠄢󠄶󠅜󠅇󠄳󠄱󠅡󠅧󠅀󠅙󠅇󠅛󠅑󠅊󠅔󠄠󠅜󠅈󠅨󠅁󠅪󠄳󠅄󠄝󠄷󠄸󠄧󠅟󠅁󠅣󠄩󠅦󠅝󠅨󠅙󠅗󠅀󠅪󠄡󠅀󠅚󠅧󠅕󠅄󠅧󠄝󠅢󠄡󠅇󠄶󠅪󠅇󠄳󠄴󠄦󠄽󠄡󠄩󠄷󠅊󠄺󠅠󠅩󠄡󠅘󠄷󠅊󠅟󠄝󠅑󠅝󠅘󠅑󠅉󠅙󠅜󠅁󠅑󠅠󠅠󠄡󠅄󠅓󠅃󠅝󠄱󠅩󠄲󠅂󠅆󠅦󠅇󠅇󠄺󠅂󠄝󠄢󠄶󠅩󠅇󠄳󠄳󠅥󠅖󠅆󠅤󠄼󠄳󠅏󠅩󠄱󠅉󠄹󠅪󠅠󠅥󠄱󠅀󠄶󠄳󠄹󠄼󠅢󠄾󠅣󠄸󠅕󠅅󠄣󠅕󠅡󠅩󠄠󠅏󠅅󠅢󠅄󠅞󠅢󠄳󠄢󠅉󠅛󠅕󠄦󠅂󠅘󠅉󠅁󠅂󠅘󠅓󠄣󠅘󠄱󠄾󠄴󠅊󠅘󠄾󠅄󠄵󠄢󠄽󠄴󠅗󠄢󠄾󠅚󠅓󠄣󠄿󠅇󠅂󠅚󠅉󠅚󠄵󠅧󠄾󠅇󠄵󠄡󠄾󠄴󠄵󠄥󠄽󠅄󠄱󠅧󠄾󠅝󠄵󠄢󠄾󠅚󠄹󠅪󠄿󠄴󠅊󠅚󠄿󠄷󠅊󠅝󠄽󠅇󠅁󠄣󠄽󠅝󠄺󠅚󠄽󠄢󠅅󠄢󠄾󠄴󠅁󠄠󠄿󠅄󠅉󠄡󠄾󠄴󠅆󠅜󠄾󠅄󠅅󠅪󠅉󠅚󠅁󠄣󠄾󠄢󠄶󠅚󠅇󠄳󠄵󠄴󠅙󠄿󠅞󠄾󠅖󠄾󠄱󠄢󠅈󠄳󠅁󠅕󠅀󠅊󠅔󠅦󠅦󠅃󠄶󠅡󠄻󠅄󠄽󠄷󠅗󠄣󠄦󠅒󠅢󠅗󠅙󠅔󠄱󠄷󠅧󠅧󠅈󠅧󠄩󠄹󠅥󠄹󠅔󠅘󠅊󠄻󠄾󠅘󠅊󠅆󠅗󠅗󠅏󠅁󠅏󠅦󠄸󠄼󠅊󠄩󠅤󠅇󠅥󠄳󠄱󠅝󠅨󠄝󠅊󠅖󠅝󠄦󠄠󠅇󠅤󠅖󠄥󠅖󠅏󠅞󠅆󠄷󠄴󠅧󠄻󠄽󠄲󠅇󠅖󠅨󠅀󠅩󠅁󠄸󠅘󠅘󠅓󠄡󠅗󠅗󠄲󠅂󠅗󠄡󠄵󠅂󠄵󠄸󠅕󠅓󠄺󠅛󠄹󠅉󠅕󠅅󠄣󠄥󠄧󠅢󠅛󠅚󠅁󠅃󠅚󠄤󠅇󠅒󠄹󠄠󠅨󠄥󠄾󠅛󠄩󠅙󠄦󠅩󠄲󠅂󠅢󠄶󠄡󠅘󠅓󠅜󠅗󠅗󠄩󠅏󠄿󠄣󠅣󠅃󠅉󠅄󠅙󠅆󠅆󠅄󠅪󠄩󠅝󠄹󠄱󠅝󠅂󠅙󠅟󠅄󠅛󠄣󠄼󠄻󠅞󠅖󠅕󠅅󠅗󠅨󠄵󠄱󠅕󠅂󠄢󠅢󠄺󠅘󠅜󠄶󠅙󠅛󠅉󠅇󠄵󠄹󠅉󠅈󠄾󠄤󠅁󠄴󠅛󠄥󠄿󠅄󠄽󠄡󠄿󠅄󠅛󠄢󠄽󠄢󠄾󠅛󠅉󠄢󠄾󠅙󠅊󠄴󠄶󠅛󠅉󠄢󠅅󠅨󠄾󠅪󠄵󠄡󠄾󠅇󠄽󠄡󠄿󠄴󠅔󠅛󠄾󠅚󠅛󠄥󠅉󠅇󠅉󠅨󠄾󠅇󠅉󠄥󠄾󠅄󠅁󠅧󠄽󠅄󠅂󠅚󠄽󠅝󠅅󠅨󠄽󠅇󠅁󠅩󠅊󠅚󠅅󠅪󠅉󠅪󠅗󠅨󠄾󠅚󠅗󠅩󠄽󠅪󠅜󠅚󠅊󠄷󠅆󠅘󠅉󠄡󠅗󠅘󠄱󠄧󠄠󠅅󠄹󠄥󠄝󠄳󠅛󠄶󠅏󠅛󠄼󠅗󠅛󠅀󠅑󠅚󠅢󠅓󠅙󠅅󠅕󠄱󠅓󠅡󠅀󠄲󠄷󠅔󠄢󠅥󠅅󠄡󠅦󠄳󠅀󠄦󠄥󠄹󠅑󠅁󠅜󠅟󠅉󠅇󠅃󠅚󠅉󠅇󠅆󠅉󠄹󠄲󠅡󠄵󠄥󠅥󠅩󠄠󠅛󠄳󠅡󠄵󠅦󠄷󠅜󠅉󠄷󠄩󠅨󠄦󠅑󠅥󠅁󠅝󠅟󠅈󠅑󠄱󠄼󠄷󠅒󠄡󠅕󠄵󠅓󠅊󠄧󠅔󠅩󠅖󠅆󠄶󠅀󠅔󠅉󠅈󠄾󠅉󠄹󠄱󠅞󠄻󠄾󠅞󠄾󠅩󠅨󠄿󠅕󠅃󠅅󠅞󠅓󠅈󠅁󠅀󠅘󠅄󠅔󠄤󠅉󠅟󠅗󠅤󠄵󠅃󠄨󠅗󠄥󠅛󠅔󠅥󠄱󠄱󠅕󠅆󠅊󠅃󠅏󠅃󠅝󠄨󠅉󠅈󠄺󠅉󠄹󠄶󠅣󠅤󠅂󠅣󠅡󠅠󠅑󠄼󠅖󠄧󠄡󠅅󠅣󠅖󠅊󠅧󠅃󠅝󠄢󠅓󠅥󠅀󠅓󠄨󠄹󠄽󠅊󠅢󠅆󠄨󠅄󠄽󠄴󠄱󠅕󠄶󠄶󠅒󠅡󠅏󠅥󠅕󠅠󠄷󠄶󠅘󠄵󠄷󠄶󠅪󠅕󠄵󠄱󠄠󠄾󠄢󠅊󠅚󠄾󠅄󠅊󠅘󠅊󠅇󠅆󠅙󠅊󠅄󠅓󠅨󠄽󠅚󠄾󠅛󠄾󠄢󠅂󠅘󠅉󠅪󠄾󠅛󠅊󠄴󠄽󠅧󠄾󠅪󠅂󠅘󠄾󠅝󠄾󠅜󠄾󠄷󠅁󠄡󠄿󠅄󠄺󠅛󠅉󠅪󠄱󠄠󠄽󠅄󠄵󠅩󠄾󠅚󠄱󠄤󠅊󠅚󠄲󠅛󠄾󠄴󠅗󠄢󠅉󠅪󠅅󠅨󠅊󠅚󠄱󠄤󠄾󠅝󠄶󠅛󠅊󠅚󠄹󠄣󠅉󠅇󠄾󠅉󠄹󠅁󠄼󠅒󠅢󠅉󠄸󠄲󠅏󠅞󠄦󠅞󠅣󠅟󠄺󠄲󠅪󠅆󠄻󠅆󠄳󠅥󠅁󠅞󠅩󠄠󠅉󠅛󠄦󠅡󠄿󠅗󠄴󠅪󠄽󠄤󠅊󠅝󠄧󠅨󠄲󠅡󠅠󠄺󠅨󠄷󠄶󠅛󠅟󠄢󠄶󠅜󠅇󠄳󠄲󠅣󠅁󠅪󠄳󠅉󠅜󠅢󠅏󠅊󠅥󠅑󠅈󠅉󠄷󠄲󠅂󠄼󠅒󠅏󠅦󠄤󠅙󠅡󠅆󠄾󠅨󠄦󠅥󠅪󠄽󠅓󠄝󠄷󠅈󠅅󠅔󠄢󠄿󠅤󠄲󠅘󠄻󠄷󠄶󠅪󠅇󠄳󠄲󠅃󠅝󠅊󠅀󠄡󠄝󠅃󠅧󠄢󠄱󠄽󠄡󠅖󠅉󠄧󠅧󠅠󠅙󠄷󠄲󠅊󠄻󠅥󠄷󠄾󠅚󠅥󠅉󠅉󠅝󠅧󠅠󠅁󠅝󠄤󠄿󠄦󠅛󠅝󠅛󠅆󠅔󠅇󠄶󠅩󠅇󠄳󠄴󠅕󠄷󠅃󠅂󠅑󠅆󠅘󠄨󠅕󠄽󠅧󠅕󠄺󠅊󠄣󠅟󠅥󠄷󠄦󠅩󠄺󠅏󠅅󠄼󠄥󠅦󠄣󠅇󠅔󠅢󠅏󠅢󠄦󠅥󠄝󠄝󠄵󠄼󠅉󠅧󠄳󠅖󠅡󠅂󠅘󠅉󠅂󠅗󠅗󠅉󠅈󠄾󠄤󠅁󠄴󠄽󠅧󠄽󠅇󠄹󠄤󠄾󠅚󠅔󠅛󠄾󠅪󠅉󠅩󠄿󠅄󠄵󠄥󠄾󠄴󠄽󠄤󠅊󠅄󠅁󠄡󠄽󠄴󠅊󠅚󠄾󠅪󠄹󠄠󠄾󠅄󠄵󠄣󠄾󠅪󠅅󠅩󠄽󠅚󠄱󠅪󠅉󠅪󠄱󠄥󠅊󠄷󠅆󠅚󠄽󠅄󠄹󠄠󠅉󠅪󠅅󠄣󠄾󠅝󠄶󠅘󠄽󠅝󠄽󠄤󠄾󠅪󠄺󠅜󠅉󠄢󠄽󠅪󠅊󠅚󠅆󠅚󠄽󠄢󠅆󠅘󠅉󠄡󠅗󠅘󠄱󠄠󠅛󠄥󠄠󠅉󠄸󠅧󠅗󠅘󠄾󠅏󠄧󠅆󠅗󠅦󠄽󠄨󠅓󠄷󠅟󠅢󠅝󠄲󠅠󠄢󠅜󠄵󠅡󠅑󠅓󠄼󠅕󠅟󠄠󠄾󠅖󠅃󠄽󠅈󠅈󠅩󠄷󠅢󠅉󠅇󠅃󠅚󠅉󠅇󠅆󠅉󠄹󠄿󠅁󠄣󠅑󠅥󠅓󠅤󠄢󠄵󠅔󠅁󠄥󠄸󠄼󠅞󠅟󠄷󠅧󠅙󠅔󠄹󠄦󠄡󠄹󠅖󠅘󠄿󠅥󠄼󠅙󠅠󠅪󠅧󠅠󠅣󠅓󠅗󠅧󠅗󠄾󠅤󠅜󠅢󠅉󠅈󠄾󠅉󠄹󠄼󠅚󠄺󠄲󠅠󠅏󠅦󠅦󠅛󠅠󠅅󠅞󠄝󠅒󠄲󠅇󠅨󠄝󠄵󠅒󠄷󠅄󠅚󠄴󠅂󠄸󠅚󠄹󠄥󠅠󠅢󠄺󠅄󠅕󠅈󠄻󠄿󠄿󠅟󠅚󠅉󠄝󠄸󠅉󠅈󠄺󠅉󠄹󠄾󠄼󠅆󠄻󠅟󠅔󠅑󠅒󠄹󠅀󠄷󠅓󠅝󠅡󠄼󠅚󠄝󠅦󠄠󠄻󠅇󠅔󠄳󠅆󠅧󠅞󠄩󠅥󠅥󠅘󠅟󠅥󠅆󠅨󠄡󠅪󠄧󠄦󠄡󠅓󠅧󠅖󠅢󠅠󠄷󠄶󠅘󠄷󠄵󠄲󠅘󠅓󠄣󠅘󠄱󠄽󠄴󠅛󠅧󠅉󠅚󠄹󠄣󠄽󠄴󠅅󠄤󠄿󠅄󠅗󠄡󠅉󠅚󠄽󠄣󠅊󠅝󠄽󠄠󠅊󠅄󠄱󠄡󠅊󠅝󠄵󠄤󠄽󠅇󠄶󠅜󠄾󠅚󠄵󠄠󠄽󠅚󠄹󠄡󠄽󠅇󠄾󠅚󠄾󠅪󠅜󠅘󠅊󠅚󠄽󠄡󠄽󠅝󠄺󠅝󠄽󠅇󠅁󠅨󠅉󠅄󠄵󠅪󠄽󠅇󠄾󠅙󠄽󠄴󠅁󠄤󠅊󠅄󠅘󠅝󠄽󠅄󠄽󠄠󠄽󠅇󠄶󠅚󠅇󠄳󠄵󠄳󠄺󠅄󠅗󠄼󠅡󠄹󠄺󠅃󠄿󠅥󠄣󠅠󠄹󠅂󠅤󠅝󠄿󠅑󠄧󠄷󠅜󠅡󠄢󠅉󠅦󠄿󠄿󠅃󠄣󠅏󠄦󠅒󠅑󠅁󠅊󠄳󠄵󠅣󠅩󠄢󠅩󠅅󠄩󠅘󠅊󠄻󠄾󠅘󠅊󠅆󠅗󠅗󠅠󠅀󠅝󠅕󠄶󠄤󠅁󠄤󠅢󠅀󠅓󠄣󠅪󠅜󠅇󠄷󠄳󠅩󠄧󠅃󠅧󠄹󠅣󠅊󠅁󠄽󠄝󠅥󠄢󠅨󠄽󠅀󠅚󠄡󠅄󠅄󠄧󠄴󠅣󠄲󠅛󠅃󠅔󠅘󠅓󠄡󠅗󠅗󠅊󠅊󠅂󠄴󠄳󠅙󠅣󠅄󠅑󠄡󠅦󠄻󠅞󠅡󠄢󠄡󠄾󠅝󠅑󠅁󠅤󠄷󠄦󠄝󠄝󠅞󠅁󠄶󠄩󠅠󠅓󠄦󠅡󠅕󠅊󠅆󠅨󠅓󠅁󠄲󠅑󠄡󠄲󠅘󠅓󠅜󠅗󠅗󠅁󠅂󠅆󠅡󠄨󠄠󠄦󠅝󠄿󠄽󠄻󠅗󠄼󠄶󠄧󠄣󠅕󠅑󠄴󠅠󠅀󠄿󠄷󠅘󠅙󠅘󠅤󠅙󠄼󠄲󠅈󠅃󠅧󠄝󠅤󠅛󠅤󠄻󠅞󠄠󠄵󠅡󠅉