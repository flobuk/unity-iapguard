using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using SimpleJSON;

namespace FLOBUK.IAPGUARD.Demo
{
    using UnityEngine.Purchasing;

    /// <summary>
    /// Unity IAP demo implementation integrated with IAPGUARD server receipt validation.
    /// </summary>
    public class IAPManager : MonoBehaviour
    {
        /// <summary>
        /// Reference to this manager instance.
        /// </summary>
        public static IAPManager Instance { get; private set; }

        /// <summary>
        /// Callback for printing debug messages to the UI.
        /// </summary>
        public static event Action<Color, string> debugCallback;

        /// <summary>
        /// Fired when Unity IAP initialization completes.
        /// </summary>
        public static event Action initializeSucceededEvent;

        /// <summary>
        /// Fired when Unity IAP initialization fails, providing error text.
        /// </summary>
        public static event Action<string> initializeFailedEvent;

        /// <summary>
        /// Fired when a purchase succeeds, delivering its Product instance / server response (if present) / new flag (false = restore).
        /// </summary>
        // You would do your custom purchase handling by subscribing to this event.
        // For example when not making use of IAPGUARD's User Inventory feature, save the purchase on device for offline mode.
        // Unlocking the reward in your UI, activating something for the user, or anything else you would want the Product to do!
        // Since purchase callbacks can happen or complete anywhere, your purchase handler should also be ready in every scene.
        public static event Action<Product, JSONNode, bool> purchaseSucceededEvent;

        /// <summary>
        /// Fired when a purchase fails, providing requested Product and error text.
        /// </summary>
        public static event Action<Product, string> purchaseFailedEvent;

        /// <summary>
        /// Product identifiers to be initialized with App Stores.
        /// </summary>
        public List<CatalogItem> catalogItems = new List<CatalogItem>();

        /// <summary>
        /// Reference to Unity IAP StoreController.
        /// </summary>
        public StoreController controller;

        //retry policy to be used for store reconnecting
        private ExponentialBackOffRetryPolicy retryPolicy = new ExponentialBackOffRetryPolicy();
        //products fetched from the App Store
        private List<Product> storeProducts;
        //whether all initialization steps passed
        private bool isInitialized = false;


        //create a persistent script instance
        void Awake()
        {
            //make sure we keep one instance of this script
            if (Instance)
            {
                Destroy(gameObject);
                return;
            }
            DontDestroyOnLoad(gameObject);

            //set static reference
            Instance = this;

            Initialize();
        }


        /// <summary>
        /// Initialize Unity IAP with demo products.
        /// </summary>
        public async void Initialize()
        {
            //setup ran already
            if (controller != null)
            {
                //but not fully initialized yet
                if (!isInitialized)
                {
                    DebugLogText(Color.white, "Retrying billing initialization...");

                    //retry from last step
                    if (storeProducts == null)
                        FetchProducts();
                    else
                        OnProductsFetched(storeProducts);
                }

                return;
            }

            #if UNITY_EDITOR
                DebugLogText(Color.red, "In-App Purchasing cannot initialize in the Unity Editor, deploy to a mobile device.");
            #endif

            //initialize Unity IAP
            controller = UnityIAPServices.StoreController();
            controller.SetStoreReconnectionRetryPolicyOnDisconnection(retryPolicy);
            controller.ProcessPendingOrdersOnPurchasesFetched(true);

            //subscribe to IAP callbacks
            controller.OnStoreDisconnected += OnInitializeFailed;
            controller.OnProductsFetched += OnProductsFetched;
            controller.OnProductsFetchFailed += OnProductsFetchFailed;
            controller.OnPurchasesFetched += OnPurchasesFetched;
            controller.OnPurchasesFetchFailed += OnPurchasesFetchFailed;
            controller.OnPurchasePending += OnPurchasePending;
            controller.OnPurchaseFailed += OnPurchaseFailed;
            controller.OnPurchaseDeferred += OnPurchaseDeferred;
            controller.OnPurchaseConfirmed += OnPurchaseConfirmed;

            await controller.Connect();

            //initialize IAPGUARD
            if (IAPGuard.Instance != null)
            {
                IAPGuard.Instance.Initialize(controller);
                IAPGuard.validationCallback += OnServerValidationResult;
            }

            FetchProducts();
        }


        /// <summary>
        /// Purchase a product via StoreController.
        /// Includes additional retry mechanic in case initialization did not pass yet.
        /// </summary>
        public void Purchase(string productId)
        {
            if (!isInitialized)
            {
                OnPurchaseFailed(productId, "Billing is not available, please try again later.");
                Initialize();
                return;
            }

            Product product = controller.GetProducts().FirstOrDefault(product => product.definition.id == productId);
            if (product == null)
            {
                OnPurchaseFailed(productId, "The Product you are trying to purchase does not exist.");
                return;
            }

            controller.PurchaseProduct(product);
        }


        /// <summary>
        /// Request re-validation of local receipts on the server, in case they do not match.
        /// Do not call this on every app launch! This should be manually triggered by the user.
        /// </summary>
        public void RestoreTransactions()
        {
            if (!isInitialized)
            {
                OnPurchaseFailed("Restore", "Billing is not available, please try again later.");
                Initialize();
                return;
            }

            DebugLogText(Color.white, "Trying to restore transactions...");
            
            #if UNITY_IOS
			    controller.RestoreTransactions((result, message) =>
                {
                    DebugLogText(Color.white, "RestoreTransactions result: " + result + ". (optional) Message: " + message);

                    if (result == true)
                        IAPGuard.Instance.RequestRestore();
                });
            #else
                IAPGuard.Instance.RequestRestore();
            #endif
        }


        /// <summary>
        /// Returns whether a product is determined as owned by IAPGUARD, with User Inventory enabled.
        /// If User Inventory is disabled, initiates a local CheckEntitlement call on the StoreController.
        /// Your receiving script should therefore also subscribe to StoreController.OnCheckEntitlement.
        /// </summary>
        public bool? IsPurchased(string productId)
        {
            if (!isInitialized)
                return null;

            if (IAPGuard.Instance.inventoryRequestType != InventoryRequestType.Disabled)
                return IAPGuard.Instance.IsOwned(productId);

            controller.CheckEntitlement(controller.GetProductById(productId));
            return null;
        }


        //utility method for reading first product in cart
        private Product GetFirstProductInOrder(Order order)
        {
            return order.CartOrdered.Items().First().Product;
        }


        //create product catalog for fetch
        private void FetchProducts()
        {
            //configure product catalog
            List<ProductDefinition> productsRequest = new List<ProductDefinition>();
            foreach (CatalogItem item in catalogItems)
            {
                productsRequest.Add(new ProductDefinition(item.id, item.type));
            }

            controller.FetchProducts(productsRequest, retryPolicy);
        }


        //StoreController.OnProductsFetched
        private void OnProductsFetched(List<Product> products)
        {
            controller.FetchPurchases();
        }


        //StoreController.OnPurchasesFetched
        //fired when Unity IAP initialization completes successfully
        private void OnPurchasesFetched(Orders orders)
        {
            if (!isInitialized)
            { 
                isInitialized = true;
                initializeSucceededEvent?.Invoke();

                DebugLogText(Color.green, "In-App Purchasing successfully initialized");
            }
        }


        //StoreController.OnStoreDisconnected
        private void OnInitializeFailed(StoreConnectionFailureDescription error)
        {
            DebugLogText(Color.red, $"In-App Purchasing initialize failed: {error.Message}");

            initializeFailedEvent?.Invoke(error.Message);
        }


        //StoreController.OnProductsFetchFailed
        private void OnProductsFetchFailed(ProductFetchFailed error)
        {
            DebugLogText(Color.red, $"In-App Purchasing product fetch failed: {error.FailureReason}");

            foreach(ProductDefinition def in error.FailedFetchProducts)
            {
                DebugLogText(Color.yellow, def.id + " with storeSpecificId: " + def.storeSpecificId);
            }

            initializeFailedEvent?.Invoke(error.FailureReason);
        }


        //StoreController.OnPurchasesFetchFailed
        private void OnPurchasesFetchFailed(PurchasesFetchFailureDescription error)
        {
            DebugLogText(Color.red, $"In-App Purchasing purchases fetch failed: {error.FailureReason}, {error.Message}");

            initializeFailedEvent?.Invoke(error.FailureReason + ", " + error.Message);
        }


        //StoreController.OnPurchaseFailed
        private void OnPurchaseFailed(FailedOrder order)
        {
            DebugLogText(Color.red, $"Purchase failed - Product: '{GetFirstProductInOrder(order).definition.id}', PurchaseFailureDescription: {order.FailureReason}, {order.Details}");

            purchaseFailedEvent?.Invoke(GetFirstProductInOrder(order), order.Details);
        }


        //override in case billing has not initialized yet
        //e.g. due to network connection issues or not being logged in on mobile device
        private void OnPurchaseFailed(string productId, string error)
        {
            DebugLogText(Color.red, $"Purchase failed - Product: '{productId}', PurchaseFailureDescription: {error}");

            purchaseFailedEvent?.Invoke(null, error);
        }


        //StoreController.OnPurchaseDeferred
        private void OnPurchaseDeferred(DeferredOrder order)
        {
            DebugLogText(Color.red, $"Purchase deferred - Product: '{GetFirstProductInOrder(order).definition.id}'");
        }


        //StoreController.OnPurchasePending
        //pending purchases have to be processed with local and server-side validation
        private void OnPurchasePending(PendingOrder order)
        {
            //get the purchase state from IAPGUARD
            PurchaseState state = IAPGuard.Instance.RequestPurchase(order);
            Product product = GetFirstProductInOrder(order);

            //handle what happens with the product next
            switch (state)
            {
                //local validation passed and server validation is not supported, or validation is not supported at all
                //grant purchase immediately since there is nothing we could validate
                case PurchaseState.Purchased:
                    DebugLogText(Color.green, "Product purchase '" + product.definition.storeSpecificId + "' finished locally.");
                    purchaseSucceededEvent?.Invoke(product, null, true);
                    break;

                //transaction is still pending (not paid for yet): leave it open for Unity IAP to be processed later again
                //or it is currently validated by IAPGUARD in the background, which will fire its serverCallback when done processing
                case PurchaseState.Pending:
                    DebugLogText(Color.white, "Product purchase '" + product.definition.storeSpecificId + "' is pending.");
                    return;

                //transaction invalid or failed validation locally
                case PurchaseState.Failed:
                    DebugLogText(Color.red, "Product purchase '" + product.definition.storeSpecificId + "' deemed as invalid.");
                    purchaseFailedEvent?.Invoke(product, "Product could not be validated locally.");
                    break;
            }

            //with the transaction finished (without server validation) or failed, confirm to close it
            controller.ConfirmPurchase(order);
        }


        //StoreController.OnPurchaseConfirmed
        private void OnPurchaseConfirmed(Order order)
        {
            switch (order)
            {
                //in case Unity IAP failed the transaction right away, display it to the user
                case FailedOrder failedOrder:
                    OnPurchaseFailed(failedOrder);
                    break;

                //the transaction was processed in OnPurchasePending, nothing else needs to be done
                //product rewards should have been granted already via purchaseSucceededEvent callback
                case ConfirmedOrder confirmedOrder:
                    DebugLogText(Color.green, "Product purchase confirmed: " + GetFirstProductInOrder(confirmedOrder).definition.id);
                    break;
            }
        }


        //IAPGuard.validationCallback
        //incoming server-side receipt validation result from IAPGUARD
        private void OnServerValidationResult(bool success, Product product, JSONNode serverData)
        {
            if (success)
            {
                //IAPGUARD receipt validation passed, reward can be granted
                purchaseSucceededEvent?.Invoke(product, serverData, true);
            }
            else
            {                   
                //IAPGUARD receipt validation failed, pass error object or raw string if empty
                purchaseFailedEvent?.Invoke(product, serverData != null ? serverData.ToString() : "Unexpected Response.");
            }
        }


        //callback for UI display purposes
        private void DebugLogText(Color color, string text)
        {
            string hexColor = ColorUtility.ToHtmlStringRGB(color);
            switch(hexColor)
            {
                case "FF0000": //red
                    Debug.LogError(text);
                    break;

                case "FFEB04": //yellow
                    Debug.LogWarning(text);
                    break;

                default: //white
                    Debug.Log(text);
                    break;
            }

            debugCallback?.Invoke(color, text);
        }
    }


    /// <summary>
    /// Definition for product catalog item.
    /// We do not make use of all fields from ProductCatalogItem.
    /// </summary>
    [Serializable]
    public class CatalogItem
    {
        public string id;

        public ProductType type;
    }
}