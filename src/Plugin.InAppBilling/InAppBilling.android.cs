using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.BillingClient.Api;
using Android.Content;
using static Android.BillingClient.Api.BillingClient;
using BillingResponseCode = Android.BillingClient.Api.BillingResponseCode;
#if NET
using Microsoft.Maui.ApplicationModel;
#else
using Xamarin.Essentials;
#endif

namespace Plugin.InAppBilling
{
    /// <summary>
    /// Implementation for Feature
    /// </summary>
    [Preserve(AllMembers = true)]
    public class InAppBillingImplementation : BaseInAppBilling
    {
        /// <summary>
        /// Gets or sets a callback for out of band purchases to complete.
        /// </summary>
        public static Action<BillingResult, List<InAppBillingPurchase>> OnAndroidPurchasesUpdated { get; set; } = null;

        /// <summary>
        /// Gets the context, aka the currently activity.
        /// This is set from the MainApplication.cs file that was laid down by the plugin
        /// </summary>
        /// <value>The context.</value>
        static Activity Activity =>
            Platform.CurrentActivity ?? throw new NullReferenceException("Current Activity is null, ensure that the MainActivity.cs file is configuring Xamarin.Essentials/.NET MAUI in your source code so the In App Billing can use it.");

        static Context Context => Application.Context;

        /// <summary>
        /// Default Constructor for In App Billing Implementation on Android
        /// </summary>
        public InAppBillingImplementation()
        {

        }


        BillingClient BillingClient { get; set; }
        BillingClient.Builder BillingClientBuilder { get; set; }
        /// <summary>
        /// Determines if it is connected to the backend actively (Android).
        /// </summary>
        public override bool IsConnected { get; set; }
        TaskCompletionSource<(BillingResult billingResult, IList<Purchase> purchases)> tcsPurchase;
        TaskCompletionSource<bool> tcsConnect;
        /// <summary>
        /// Connect to billing service
        /// </summary>
        /// <returns>If Success</returns>
        public override Task<bool> ConnectAsync(bool enablePendingPurchases = true)
        {
            tcsPurchase?.TrySetCanceled();
            tcsPurchase = null;

            tcsConnect?.TrySetCanceled();
            tcsConnect = new TaskCompletionSource<bool>();

            BillingClientBuilder = NewBuilder(Context);
            BillingClientBuilder.SetListener(OnPurchasesUpdated);
            if (enablePendingPurchases)
                BillingClient = BillingClientBuilder.EnablePendingPurchases().Build();
            else
                BillingClient = BillingClientBuilder.Build();

            BillingClient.StartConnection(OnSetupFinished, OnDisconnected);

            return tcsConnect.Task;

            void OnSetupFinished(BillingResult billingResult)
            {
                Console.WriteLine($"Billing Setup Finished : {billingResult.ResponseCode} - {billingResult.DebugMessage}");
                IsConnected = billingResult.ResponseCode == BillingResponseCode.Ok;
                tcsConnect?.TrySetResult(IsConnected);
            }

            void OnDisconnected()
            {
                IsConnected = false;
            }
        }

        public void OnPurchasesUpdated(BillingResult billingResult, IList<Android.BillingClient.Api.Purchase> purchases)
        {
            tcsPurchase?.TrySetResult((billingResult, purchases));

            if (OnAndroidPurchasesUpdated == null)
                return;

            OnAndroidPurchasesUpdated?.Invoke(billingResult, purchases?.Select(p => p.ToIABPurchase())?.ToList());
        }

        /// <summary>
        /// Disconnect from the billing service
        /// </summary>
        /// <returns>Task to disconnect</returns>
        public override Task DisconnectAsync()
        {
            try
            {
                BillingClientBuilder?.Dispose();
                BillingClientBuilder = null;
                BillingClient?.EndConnection();
                BillingClient?.Dispose();
                BillingClient = null;
                IsConnected = false;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Unable to disconnect: {ex.Message}");
            }

            return Task.CompletedTask;
        }


        /// <summary>
        /// Gets or sets if in testing mode. Only for UWP
        /// </summary>
        public override bool InTestingMode { get; set; }

        /// <summary>
        /// Get product information of a specific product
        /// </summary>
        /// <param name="productIds">Sku or Id of the product</param>
        /// <param name="itemType">Type of product offering</param>
        /// <returns></returns>
        public async override Task<IEnumerable<InAppBillingProduct>> GetProductInfoAsync(ItemType itemType, params string[] productIds)
        {
            if (BillingClient == null || !IsConnected)
            {
                throw new InAppBillingPurchaseException(PurchaseError.ServiceUnavailable, "You are not connected to the Google Play App store.");
            }


            var skuType = itemType switch
            {
                ItemType.InAppPurchase => ProductType.Inapp,
                ItemType.InAppPurchaseConsumable => ProductType.Inapp,
                _ => ProductType.Subs
            };

            if (skuType == ProductType.Subs)
            {
                var result = BillingClient.IsFeatureSupported(FeatureType.Subscriptions);
                ParseBillingResult(result);
            }


            var productList = productIds.Select(p => QueryProductDetailsParams.Product.NewBuilder()
                .SetProductType(skuType)
                .SetProductId(p)
                .Build()).ToList();

            if (productList == null || productList.Count == 0)
            {
                return Enumerable.Empty<InAppBillingProduct>();
            }

            var skuDetailsParams = QueryProductDetailsParams.NewBuilder().SetProductList(productList);

            var skuDetailsResult = await BillingClient.QueryProductDetailsAsync(skuDetailsParams.Build());
            ParseBillingResult(skuDetailsResult?.Result, IgnoreInvalidProducts);


            return skuDetailsResult.ProductDetails.Select(product => product.ToIAPProduct());
        }


        public override async Task<IEnumerable<InAppBillingPurchase>> GetPurchasesAsync(ItemType itemType)
        {
            if (BillingClient == null)
                throw new InAppBillingPurchaseException(PurchaseError.ServiceUnavailable, "You are not connected to the Google Play App store.");

            var skuType = itemType switch
            {
                ItemType.InAppPurchase => ProductType.Inapp,
                ItemType.InAppPurchaseConsumable => ProductType.Inapp,
                _ => ProductType.Subs
            };

            var query = QueryPurchasesParams.NewBuilder().SetProductType(skuType).Build();
            var purchasesResult = await BillingClient.QueryPurchasesAsync(query);

            ParseBillingResult(purchasesResult.Result);

            return purchasesResult.Purchases.Select(p => p.ToIABPurchase());
        }

        /// <summary>
        /// Android only: Returns the most recent purchase made by the user for each SKU, even if that purchase is expired, canceled, or consumed.
        /// </summary>
        /// <param name="itemType">Type of product</param>
        /// <returns>The current purchases</returns>
        public override async Task<IEnumerable<InAppBillingPurchase>> GetPurchasesHistoryAsync(ItemType itemType)
        {
            if (BillingClient == null)
                throw new InAppBillingPurchaseException(PurchaseError.ServiceUnavailable, "You are not connected to the Google Play App store.");

            var skuType = itemType switch
            {
                ItemType.InAppPurchase => ProductType.Inapp,
                ItemType.InAppPurchaseConsumable => ProductType.Inapp,
                _ => ProductType.Subs
            };

            //TODO: Binding needs updated
            var purchasesResult = await BillingClient.QueryPurchaseHistoryAsync(skuType);


            return purchasesResult?.PurchaseHistoryRecords?.Select(p => p.ToIABPurchase()) ?? new List<InAppBillingPurchase>();
        }

        /// <summary>
        /// (Android specific) Upgrade/Downgrade/Change a previously purchased subscription
        /// </summary>
        /// <param name="newProductId">Sku or ID of product that will replace the old one</param>
        /// <param name="purchaseTokenOfOriginalSubscription">Purchase token of original subscription</param>
        /// <param name="prorationMode">Proration mode (1 - ImmediateWithTimeProration, 2 - ImmediateAndChargeProratedPrice, 3 - ImmediateWithoutProration, 4 - Deferred)</param>
        /// <returns>Purchase details</returns>
        public override async Task<InAppBillingPurchase> UpgradePurchasedSubscriptionAsync(string newProductId, string purchaseTokenOfOriginalSubscription, SubscriptionProrationMode prorationMode = SubscriptionProrationMode.ImmediateWithTimeProration)
        {
            if (BillingClient == null || !IsConnected)
            {
                throw new InAppBillingPurchaseException(PurchaseError.ServiceUnavailable, "You are not connected to the Google Play App store.");
            }

            // If we have a current task and it is not completed then return null.
            // you can't try to purchase twice.
            if (tcsPurchase?.Task != null && !tcsPurchase.Task.IsCompleted)
            {
                return null;
            }

            var purchase = await UpgradePurchasedSubscriptionInternalAsync(newProductId, purchaseTokenOfOriginalSubscription, prorationMode);

            return purchase;
        }

        async Task<InAppBillingPurchase> UpgradePurchasedSubscriptionInternalAsync(string newProductId, string purchaseTokenOfOriginalSubscription, SubscriptionProrationMode prorationMode)
        {
            var itemType = ProductType.Subs;

            if (tcsPurchase?.Task != null && !tcsPurchase.Task.IsCompleted)
            {
                return null;
            }

            var productList = QueryProductDetailsParams.Product.NewBuilder()
               .SetProductType(itemType)
               .SetProductId(newProductId)
               .Build();

            var skuDetailsParams = QueryProductDetailsParams.NewBuilder().SetProductList(new[] { productList }).Build();

            var skuDetailsResult = await BillingClient.QueryProductDetailsAsync(skuDetailsParams);

            ParseBillingResult(skuDetailsResult.Result);

            var skuDetails = skuDetailsResult.ProductDetails.FirstOrDefault() ?? throw new ArgumentException($"{newProductId} does not exist");

            //1 - BillingFlowParams.ProrationMode.ImmediateWithTimeProration
            //2 - BillingFlowParams.ProrationMode.ImmediateAndChargeProratedPrice
            //3 - BillingFlowParams.ProrationMode.ImmediateWithoutProration
            //4 - BillingFlowParams.ProrationMode.Deferred
            //5 - BillingFlowParams.ProrationMode.ImmediateAndChargeFullPrice

            var updateParams = BillingFlowParams.SubscriptionUpdateParams.NewBuilder()
                .SetOldPurchaseToken(purchaseTokenOfOriginalSubscription)
                .SetReplaceProrationMode((int)prorationMode)
                .Build();

            var prodDetailsParams = BillingFlowParams.ProductDetailsParams.NewBuilder()
                .SetProductDetails(skuDetails)
                .SetOfferToken(skuDetails.GetSubscriptionOfferDetails()?.FirstOrDefault()?.OfferToken)
                .Build();

            var flowParams = BillingFlowParams.NewBuilder()
                .SetProductDetailsParamsList(new[] { prodDetailsParams })
                .SetSubscriptionUpdateParams(updateParams)
                .Build();

            tcsPurchase = new TaskCompletionSource<(BillingResult billingResult, IList<Purchase> purchases)>();
            var responseCode = BillingClient.LaunchBillingFlow(Activity, flowParams);

            ParseBillingResult(responseCode);

            var result = await tcsPurchase.Task;
            ParseBillingResult(result.billingResult);

            //we are only buying 1 thing.
            var androidPurchase = result.purchases?.FirstOrDefault(p => p.Products.Contains(newProductId));

            //for some reason the data didn't come back
            if (androidPurchase == null)
            {
                var purchases = await GetPurchasesAsync(itemType == ProductType.Inapp ? ItemType.InAppPurchase : ItemType.Subscription);
                return purchases.FirstOrDefault(p => p.ProductId == newProductId);
            }

            return androidPurchase.ToIABPurchase();
        }

        /// <summary>
        /// Purchase a specific product or subscription
        /// </summary>
        /// <param name="productId">Sku or ID of product</param>
        /// <param name="itemType">Type of product being requested</param>
        /// <param name="obfuscatedAccountId">Specifies an optional obfuscated string that is uniquely associated with the user's account in your app.</param>
        /// <param name="obfuscatedProfileId">Specifies an optional obfuscated string that is uniquely associated with the user's profile in your app.</param>
        /// <returns></returns>
        public async override Task<InAppBillingPurchase> PurchaseAsync(string productId, ItemType itemType, string obfuscatedAccountId = null, string obfuscatedProfileId = null, string subOfferToken = null)
        {
            if (BillingClient == null || !IsConnected)
            {
                throw new InAppBillingPurchaseException(PurchaseError.ServiceUnavailable, "You are not connected to the Google Play App store.");
            }

            // If we have a current task and it is not completed then return null.
            // you can't try to purchase twice.
            if (tcsPurchase?.Task != null && !tcsPurchase.Task.IsCompleted)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(obfuscatedProfileId) && string.IsNullOrWhiteSpace(obfuscatedAccountId))
                throw new ArgumentNullException("You must set an account id if you are setting a profile id");

            switch (itemType)
            {
                case ItemType.InAppPurchase:
                case ItemType.InAppPurchaseConsumable:
                    return await PurchaseAsync(productId, ProductType.Inapp, obfuscatedAccountId, obfuscatedProfileId);
                case ItemType.Subscription:

                    var result = BillingClient.IsFeatureSupported(FeatureType.Subscriptions);
                    ParseBillingResult(result);
                    return await PurchaseAsync(productId, ProductType.Subs, obfuscatedAccountId, obfuscatedProfileId, subOfferToken);
            }

            return null;
        }

        async Task<InAppBillingPurchase> PurchaseAsync(string productSku, string itemType, string obfuscatedAccountId = null, string obfuscatedProfileId = null, string subOfferToken = null)
        {

            var productList = QueryProductDetailsParams.Product.NewBuilder()
                .SetProductType(itemType)
                .SetProductId(productSku)
                .Build();

            var skuDetailsParams = QueryProductDetailsParams.NewBuilder().SetProductList(new[] { productList });

            var skuDetailsResult = await BillingClient.QueryProductDetailsAsync(skuDetailsParams.Build());

            ParseBillingResult(skuDetailsResult.Result);

            var skuDetails = skuDetailsResult.ProductDetails.FirstOrDefault() ?? throw new ArgumentException($"{productSku} does not exist");
            var productDetailsParamsList = itemType == ProductType.Subs ?
                BillingFlowParams.ProductDetailsParams.NewBuilder()
                .SetProductDetails(skuDetails)
                .SetOfferToken(subOfferToken ?? skuDetails.GetSubscriptionOfferDetails()?.FirstOrDefault()?.OfferToken ?? string.Empty)
                .Build()
                : BillingFlowParams.ProductDetailsParams.NewBuilder()
                .SetProductDetails(skuDetails)
                .Build();

            var billingFlowParams = BillingFlowParams.NewBuilder()
                .SetProductDetailsParamsList(new[] { productDetailsParamsList });



            if (!string.IsNullOrWhiteSpace(obfuscatedAccountId))
                billingFlowParams.SetObfuscatedAccountId(obfuscatedAccountId);

            if (!string.IsNullOrWhiteSpace(obfuscatedProfileId))
                billingFlowParams.SetObfuscatedProfileId(obfuscatedProfileId);

            var flowParams = billingFlowParams.Build();



            tcsPurchase = new TaskCompletionSource<(BillingResult billingResult, IList<Android.BillingClient.Api.Purchase> purchases)>();


            var responseCode = BillingClient.LaunchBillingFlow(Activity, flowParams);

            ParseBillingResult(responseCode);

            var result = await tcsPurchase.Task;
            ParseBillingResult(result.billingResult);

            //we are only buying 1 thing.
            var androidPurchase = result.purchases?.FirstOrDefault(p => p.Products.Contains(productSku));

            //for some reason the data didn't come back
            if (androidPurchase == null)
            {
                var purchases = await GetPurchasesAsync(itemType == ProductType.Inapp ? ItemType.InAppPurchase : ItemType.Subscription);
                return purchases.FirstOrDefault(p => p.ProductId == productSku);
            }

            return androidPurchase.ToIABPurchase();
        }


        public async override Task<IEnumerable<(string Id, bool Success)>> FinalizePurchaseAsync(params string[] transactionIdentifier)
        {
            if (BillingClient == null || !IsConnected)
                throw new InAppBillingPurchaseException(PurchaseError.ServiceUnavailable, "You are not connected to the Google Play App store.");


            var items = new List<(string Id, bool Success)>();
            foreach (var t in transactionIdentifier)
            {

                var acknowledgeParams = AcknowledgePurchaseParams.NewBuilder()
                        .SetPurchaseToken(t).Build();

                var result = await BillingClient.AcknowledgePurchaseAsync(acknowledgeParams);

                items.Add((t, ParseBillingResult(result)));
            }

            return items;
        }



        /// <summary>
        /// Consume a purchase with a purchase token.
        /// in app:{Context.PackageName}:{productSku}
        /// </summary>
        /// <param name="productId">Id or Sku of product</param>
        /// <param name="transactionIdentifier">Original Purchase Token</param>
        /// <returns>If consumed successful</returns>
        public override async Task<bool> ConsumePurchaseAsync(string productId, string transactionIdentifier)
        {
            if (BillingClient == null || !IsConnected)
            {
                throw new InAppBillingPurchaseException(PurchaseError.ServiceUnavailable, "You are not connected to the Google Play App store.");
            }


            var consumeParams = ConsumeParams.NewBuilder()
                .SetPurchaseToken(transactionIdentifier)
                .Build();

            var result = await BillingClient.ConsumeAsync(consumeParams);


            return ParseBillingResult(result.BillingResult);
        }

        static bool ParseBillingResult(BillingResult result, bool ignoreInvalidProducts = false)
        {
            if (result == null)
                throw new InAppBillingPurchaseException(PurchaseError.GeneralError);

            if ((int)result.ResponseCode == Android.BillingClient.Api.BillingClient.BillingResponseCode.NetworkError)
                throw new InAppBillingPurchaseException(PurchaseError.ServiceTimeout);//Network connection is down

            return result.ResponseCode switch
            {
                BillingResponseCode.Ok => true,
                BillingResponseCode.UserCancelled => throw new InAppBillingPurchaseException(PurchaseError.UserCancelled),//User Cancelled, should try again
                BillingResponseCode.ServiceUnavailable => throw new InAppBillingPurchaseException(PurchaseError.ServiceUnavailable),//Network connection is down
                BillingResponseCode.ServiceDisconnected => throw new InAppBillingPurchaseException(PurchaseError.ServiceDisconnected),//Network connection is down
                BillingResponseCode.ServiceTimeout => throw new InAppBillingPurchaseException(PurchaseError.ServiceTimeout),//Network connection is down
                BillingResponseCode.BillingUnavailable => throw new InAppBillingPurchaseException(PurchaseError.BillingUnavailable),//Billing Unavailable
                BillingResponseCode.ItemNotOwned => throw new InAppBillingPurchaseException(PurchaseError.NotOwned),//Item not owned
                BillingResponseCode.DeveloperError => throw new InAppBillingPurchaseException(PurchaseError.DeveloperError),//Developer Error
                BillingResponseCode.Error => throw new InAppBillingPurchaseException(PurchaseError.GeneralError),//Generic Error
                BillingResponseCode.FeatureNotSupported => throw new InAppBillingPurchaseException(PurchaseError.FeatureNotSupported),
                BillingResponseCode.ItemAlreadyOwned => throw new InAppBillingPurchaseException(PurchaseError.AlreadyOwned),
                BillingResponseCode.ItemUnavailable => ignoreInvalidProducts ? false : throw new InAppBillingPurchaseException(PurchaseError.ItemUnavailable),
                _ => false,
            };
        }
    }
}

