using SimpleJSON;
using System;
using UnityEngine;

namespace FLOBUK.ReceiptValidator.Demo
{
    using Unity.Services.Core;
    using UnityEngine.Purchasing;
    using UnityEngine.Purchasing.Extension;

    /// <summary>
    /// Unity IAP Demo Implementation.
    /// </summary>
    public class IAPManager : MonoBehaviour, IDetailedStoreListener
    {
        private static IAPManager instance;
        public static event Action<Color, string> debugCallback;
        public static event Action<bool, JSONNode> purchaseCallback;

        //product identifiers for App Stores
        [Header("Product IDs")]
        public string consumableProductId = "coins";
        public string nonconsumableProductId = "no_ads";
        public string subscriptionProductId = "abo_monthly";

        //Unity IAP references
        public IStoreController controller;
        IExtensionProvider extensions;
        ConfigurationBuilder builder;


        //return the instance of this script.
        public static IAPManager GetInstance()
        {
            return instance;
        }


        //create a persistent script instance
        void Awake()
        {
            if (instance)
            {
                Destroy(gameObject);
                return;
            }
            DontDestroyOnLoad(this);

            instance = this;
            Initialize();
        }


        //initialize Unity IAP with demo products
        public async void Initialize()
        {
            //initialized already
            if (controller != null) return;

            try
            {
                //Unity Gaming Services are required first
                await UnityServices.InitializeAsync();

                builder = ConfigurationBuilder.Instance(StandardPurchasingModule.Instance());

                builder.AddProduct(consumableProductId, ProductType.Consumable);
                builder.AddProduct(nonconsumableProductId, ProductType.NonConsumable);
                builder.AddProduct(subscriptionProductId, ProductType.Subscription);

                //initialize Unity IAP
                UnityPurchasing.Initialize(this, builder);
            }
            catch (Exception)
            {
                OnInitializeFailed(InitializationFailureReason.PurchasingUnavailable);
            }
        }


        //fired when Unity IAP initialization completes successfully
        public void OnInitialized(IStoreController controller, IExtensionProvider extensions)
        {
            DebugLogText(Color.green, "In-App Purchasing successfully initialized");
            this.controller = controller;
            this.extensions = extensions;

            //initialize ReceiptValidator
            ReceiptValidator.Instance.Initialize(controller, builder);
            ReceiptValidator.purchaseCallback += OnPurchaseResult;
            //if you are making use of user inventory
            ReceiptValidator.Instance.RequestInventory();
        }


        //fired when Unity IAP receives a purchase which is then ready for local and server-side validation
        public PurchaseProcessingResult ProcessPurchase(PurchaseEventArgs args)
        {
            Product product = args.purchasedProduct;

            //do validation, the magic happens here!
            PurchaseState state = ReceiptValidator.Instance.RequestPurchase(product);
            //handle what happens with the product next
            switch (state)
            {
                case PurchaseState.Purchased:
                    //nothing to do here: with the transaction finished at this point it means that either
                    //1) local validation passed but server validation is not supported, or
                    //2) validation is not supported at all, e.g. when running on a non-supported store
                    break;

                //transaction is pending or about to be validated on the server
                //it is important to return pending to leave the transaction open for the ReceiptValidator
                //the ReceiptValidator will fire its purchaseCallback when done processing
                case PurchaseState.Pending: 
                    DebugLogText(Color.white, "Product purchase '" + product.definition.storeSpecificId + "' is pending.");
                    return PurchaseProcessingResult.Pending;

                //transaction invalid or failed locally. Complete transaction to not validate again
                case PurchaseState.Failed: 
                    DebugLogText(Color.red, "Product purchase '" + product.definition.storeSpecificId + "' deemed as invalid.");
                    break;
            }

            //with the transaction finished (without validation) or failed, just call our purchase handler
            //we just hand over the product id to keep the expected dictionary structure consistent
            JSONObject resultData = new JSONObject();
            resultData["data"]["productId"] = product.definition.id;
            OnPurchaseResult(state == PurchaseState.Purchased, resultData);

            return PurchaseProcessingResult.Complete;
        }


        //request re-validation of local receipts on the server, in case they do not match.
        //do not call this on every app launch! This should be manually triggered by the user.
        public void RestoreTransactions()
        {
            if (controller == null)
            {
                DebugLogText(Color.yellow, "Unity IAP is not initialized yet.");
                return;
            }

            DebugLogText(Color.white, "Trying to restore transactions...");

            #if UNITY_IOS
			    extensions.GetExtension<IAppleExtensions>().RestoreTransactions((result, message) => { DebugLogText(Color.white, "RestoreTransactions result: " + result + ". Message: " + message); });
            #else
                ReceiptValidator.Instance.RequestRestore();
            #endif
        }


        //fired when Unity IAP failed to initialize.
        public void OnInitializeFailed(InitializationFailureReason error)
        {
            DebugLogText(Color.red, $"In-App Purchasing initialize failed: {error}");
        }


        //fired when Unity IAP failed to initialize.
        public void OnInitializeFailed(InitializationFailureReason error, string message)
        {
            DebugLogText(Color.red, $"In-App Purchasing initialize failed: {message}");
        }


        //fired when Unity IAP failed to process a purchase.
        public void OnPurchaseFailed(Product product, PurchaseFailureReason failureReason)
        {
            DebugLogText(Color.red, $"Purchase failed - Product: '{product.definition.id}', PurchaseFailureReason: {failureReason}");
            purchaseCallback(false, null);
        }


        //fired when Unity IAP failed to process a purchase.
        public void OnPurchaseFailed(Product product, PurchaseFailureDescription failureDescription)
        {
            DebugLogText(Color.red, $"Purchase failed - Product: '{product.definition.id}', PurchaseFailureDescription: {failureDescription.reason}, {failureDescription.message}");
            purchaseCallback(false, null);
        }


        //you would do your custom purchase handling by subscribing to the purchaseCallback event
        //for example when not making use of user inventory, save the purchase on device for offline mode
        //unlock the reward in your UI, activate something for the user, or anything else you want it to do!
        //since purchase callbacks can happen or complete anywhere, your purchase handler should also be
        //present in every scene or have DontDestroyOnLoad active as well
        public void OnPurchaseResult(bool success, JSONNode data)
        {
            purchaseCallback(success, data);
        }


        //callback for UI display purposes
        void DebugLogText(Color color, string text)
        {
            debugCallback?.Invoke(color, text);
        }
    }
}