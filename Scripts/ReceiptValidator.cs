using SimpleJSON;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace FLOBUK.ReceiptValidator
{
    using UnityEngine.Purchasing;
    using UnityEngine.Purchasing.Security;

    /// <summary>
    /// Receipt Validator implementation.
    /// Does local and then server validation for your in-app purchases.
    /// </summary>
    public class ReceiptValidator : MonoBehaviour
    {
        private static ReceiptValidator _Instance;
        public static event Action inventoryCallback;
        public static event Action<bool, JSONNode> purchaseCallback;

        const string validationEndpoint = "https://flobuk.com/validator/v1/receipt/";
        const string userEndpoint = "https://flobuk.com/validator/v1/user/";

        [Header("General Data")]
        public string appID;
        public string userID;

        [Header("User Inventory is not supported on the Free plan.", order = 0)]
        [Header("Please leave it on 'Disabled' if you didn't upgrade.", order = 1)]
        [Header("Inventory", order = 2)]
        public InventoryRequestType inventoryRequestType = InventoryRequestType.Disabled;

        Dictionary<string, PurchaseResponse> inventory = new Dictionary<string, PurchaseResponse>();

        CrossPlatformValidator localValidator = null;
        IStoreController controller;
        ConfigurationBuilder builder;

        const string lastInventoryTimestampKey = "fbrv_inventory_timestamp";
        float lastInventoryTime = -1;
        bool inventoryRequestActive = false;
        int inventoryDelay = 1800;


        /// <summary>
        /// Return the Singleton Instance.
        /// </summary>
        public static ReceiptValidator Instance
        {
            get
            {
                if (_Instance == null)
                {
                    GameObject obj = new GameObject("ReceiptValidator");
                    _Instance = obj.AddComponent<ReceiptValidator>();
                }

                return _Instance;
            }
        }


        void Awake()
        {
            if (_Instance != null)
            {
                Destroy(gameObject);
            }

            DontDestroyOnLoad(gameObject);
            _Instance = this;
        }


        /// <summary>
        /// Initialize the component by passing in a reference to Unity IAP.
        /// </summary>
        public void Initialize(IStoreController controller, ConfigurationBuilder builder)
        {
            this.controller = controller;
            this.builder = builder;

            if (IsLocalValidationSupported())
            {
                #if !UNITY_EDITOR
                    localValidator = new CrossPlatformValidator(GooglePlayTangle.Data(), AppleTangle.Data(), Application.identifier);
                #endif
            }
        }


        /// <summary>
        /// Request inventory from the server, for the user specified as 'userID'.
        /// </summary>
        public void RequestInventory()
        {
            //in case requesting inventory was disabled or limited by delay timing
            if (!CanRequestInventory())
            {
                if (Debug.isDebugBuild && inventoryRequestType != InventoryRequestType.Disabled)
                    Debug.LogWarning("Receipt Validator: CanRequestInventory returned false.");
                
                return;
            }

            //server validation is not supported on this platform, so no inventory is stored either or requests exceeded
            if (controller == null || !IsServerValidationSupported())
            {
                if (Debug.isDebugBuild) Debug.LogWarning("Receipt Validator: Inventory Request not supported.");
                return;
            }

            //no purchase detected on this account, RequestInventory call is not necessary and was cancelled
            //if you are sure that this account has purchased products, instruct the user to initiate a restore first
            if (!HasActiveReceipt() && !HasPurchaseHistory())
            {
                if(Debug.isDebugBuild) Debug.LogWarning("Receipt Validator: Inventory Request not necessary.");
                return;
            }

            inventoryRequestActive = true;
            StartCoroutine(RequestInventoryRoutine());
        }


        IEnumerator RequestInventoryRoutine()
        {
            using (UnityWebRequest www = UnityWebRequest.Get(userEndpoint + appID + "/" + userID))
            {
                www.SetRequestHeader("content-type", "application/json");
                yield return www.SendWebRequest();

                //raw JSON response
                JSONNode rawResponse = JSON.Parse(www.downloadHandler.text);
                JSONArray purchaseArray = rawResponse["purchases"].AsArray;

                inventory.Clear();
                for (int i = 0; i < purchaseArray.Count; i++)
                {
                    inventory.Add(purchaseArray[i]["data"]["productId"].Value, JsonUtility.FromJson<PurchaseResponse>(purchaseArray[i]["data"].ToString()));
                }

                SetPurchaseHistory();
            }

            lastInventoryTime = Time.realtimeSinceStartup;
            inventoryRequestActive = false;
            inventoryCallback?.Invoke();
        }


        /// <summary>
        /// Request validation of a newly bought or restored product receipt.
        /// </summary>
        public PurchaseState RequestPurchase(Product product)
        {
            //assume that the purchase is valid as default
            PurchaseState state = PurchaseState.Purchased;
            //running on unsupported store
            if (controller == null)
            {
                return state;
            }

            //nothing to validate without receipt
            if (!product.hasReceipt)
            {
                return PurchaseState.Failed;
            }

            //if local validation is supported it could return otherwise
            if (IsLocalValidationSupported() && localValidator != null)
            {
                state = LocalValidation(product);
            }
            
            //local validation was not supported or it passed as valid
            //now do the server validation, if supported and keep transaction pending
            if(state == PurchaseState.Purchased && IsServerValidationSupported())
            {
                StartCoroutine(RequestPurchaseRoutine(product));
                return PurchaseState.Pending;
            }

            //return state of local validation, or default state
            //if no validation technique was supported at all
            return state;
        }


        IEnumerator RequestPurchaseRoutine(Product product)
        {
            //if the app is closed during this time, ProcessPurchase will be
            //called again for the same purchase once the app is opened again

            JSONNode receiptData = JSON.Parse(product.receipt);
            string transactionID = receiptData["TransactionID"].Value;

            #if UNITY_IOS
            IPurchaseReceipt[] receipts = localValidator.Validate(product.receipt);
            foreach (AppleInAppPurchaseReceipt receipt in receipts)
            {
                if (product.definition.id != receipt.productID)
                    continue;

                transactionID = receipt.transactionID;
                break;
            }
            #endif

            ReceiptRequest request = new ReceiptRequest()
            {
                store = receiptData["Store"].Value,
                bid = Application.identifier,
                pid = product.definition.storeSpecificId,
                user = userID,
                type = GetType(product.definition.type),
                receipt = transactionID
            };
            string postData = JsonUtility.ToJson(request);

            JSONNode rawResponse = null;
            bool success = false;
            using (UnityWebRequest www = UnityWebRequest.Put(validationEndpoint + appID, postData))
            {
                www.SetRequestHeader("content-type", "application/json");
                yield return www.SendWebRequest();

                //raw JSON response
                try
                {
                    rawResponse = JSON.Parse(www.downloadHandler.text);
                    success = www.error == null && rawResponse != null && string.IsNullOrEmpty(rawResponse["error"]) && rawResponse.HasKey("data");
                }
                catch
                {
                    //there might be an configuration issue since the response was not valid JSON, transaction will not be finished
                    if (Debug.isDebugBuild) Debug.LogWarning("Server Receipt Validation failed for: '" + product.definition.storeSpecificId + "'.\n" + www.downloadHandler.text);
                }

                if (success)
                {
                    string productId = rawResponse["data"]["productId"].Value;
                    PurchaseResponse thisPurchase = JsonUtility.FromJson<PurchaseResponse>(rawResponse["data"].ToString());

                    //remember this userID for this session if we received a server-generated one
                    if (string.IsNullOrEmpty(userID) && rawResponse.HasKey("user"))
                        userID = rawResponse["user"].Value;

                    if (inventory.ContainsKey(productId)) inventory[productId] = thisPurchase; //already exist, replace
                    else inventory.Add(productId, thisPurchase); //add new to inventory
                }

                purchaseCallback?.Invoke(success, rawResponse);
            }

            //do not complete pending purchases but still leave them open for processing again later
            if (rawResponse == null || rawResponse.HasKey("error") && rawResponse["code"] == 10130)
            {
                yield break;
            }

            //once we have done the validation in our backend, we update the purchase status
            controller.ConfirmPendingPurchase(product);
        }


        /// <summary>
        /// Request re-validation of all product receipts available in Unity IAP locally.
        /// </summary>
        public void RequestRestore()
        {
            //running on unsupported store or not yet initialized
            if (controller == null || !IsServerValidationSupported())
            {
                return;
            }

            StartCoroutine(RequestRestoreRoutine());
        }


        IEnumerator RequestRestoreRoutine()
        {
            foreach (Product product in controller.products.all)
            {
                if (product.definition.type == ProductType.Consumable || !product.hasReceipt || inventory.ContainsKey(product.definition.id))
                    continue;

                StartCoroutine(RequestPurchaseRoutine(product));
                yield return new WaitForSecondsRealtime(UnityEngine.Random.Range(2f, 5f));
            }
        }


        /// <summary>
        /// Return current user inventory stored in memory.
        /// </summary>
        public Dictionary<string, PurchaseResponse> GetInventory()
        {
            return inventory;
        }


        /// <summary>
        /// Return whether a product is purchased or not by checking user inventory received earlier.
        /// Since there is no inventory on the Free plan or with inventory disabled, it checks for a local product receipt instead.
        /// </summary>
        public bool IsPurchased(string productId)
        {
            if (inventoryRequestType == InventoryRequestType.Disabled)
            {
                if (controller == null) return false;
                else return controller.products.WithID(productId).hasReceipt;
            }

            int[] purchaseStates = new int[] { 0, 1, 4 };
            if (inventory.ContainsKey(productId) && Array.Exists(purchaseStates, x => x == inventory[productId].status))
            {
                return true;
            }

            return false;
        }


        /// <summary>
        /// Returns whether getting inventory is currently disabled, limited or not possible.
        /// </summary>
        public bool CanRequestInventory()
        {
            //GetInventory request is already active. This call was cancelled
            if (inventoryRequestActive)
            {
                return false;
            }

            switch (inventoryRequestType)
            {
                //GetInventory call is disabled. If your plan supports User Inventory, select a different Inventory Request Type
                case InventoryRequestType.Disabled:
                    return false;

                //GetInventory call was cancelled because it has already been requested before
                case InventoryRequestType.Once:
                    if (lastInventoryTime > 0)
                    {
                        return false;
                    }
                    break;

                //GetInventory call was cancelled to prevent excessive bandwidth consumption and API limits
                case InventoryRequestType.Delay:
                    if (lastInventoryTime > 0 && Time.realtimeSinceStartup - lastInventoryTime < inventoryDelay)
                    {
                        return false;
                    }
                    break;
            }

            //All checks passed, but a user identifier has not been set
            if (string.IsNullOrEmpty(userID))
            {
                return false;
            }

            return true;
        }


        void SetPurchaseHistory()
        {
            if (PlayerPrefs.HasKey(lastInventoryTimestampKey) && inventory.Count == 0)
            {
                PlayerPrefs.DeleteKey(lastInventoryTimestampKey);
                return;
            }

            if (!PlayerPrefs.HasKey(lastInventoryTimestampKey) && inventory.Count > 0)
            {
                PlayerPrefs.SetString(lastInventoryTimestampKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            }
        }


        bool HasPurchaseHistory()
        {
            if (!PlayerPrefs.HasKey(lastInventoryTimestampKey))
                return false;

            long lastTimestamp = long.Parse(PlayerPrefs.GetString(lastInventoryTimestampKey));
            long timestampNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (timestampNow - lastTimestamp < 2628000) //2628000 seconds = 1 month
            {
                return true;
            }

            PlayerPrefs.DeleteKey(lastInventoryTimestampKey);
            return false;
        }


        bool HasActiveReceipt()
        {
            bool hasReceipt = false;

            #if UNITY_ANDROID
                foreach (Product product in controller.products.all)
                {
                    if (product.definition.type != ProductType.Consumable && product.hasReceipt)
                    {
                        hasReceipt = true;
                        break;
                    }
                }
            #elif UNITY_IOS
                IAppleConfiguration appleConfig = builder.Configure<IAppleConfiguration>();
                if (string.IsNullOrEmpty(appleConfig.appReceipt)) return false;
                byte[] appReceipt = Convert.FromBase64String(appleConfig.appReceipt);
                AppleReceipt appleReceipt = new AppleValidator(AppleTangle.Data()).Validate(appReceipt);

                if(appleReceipt.inAppPurchaseReceipts.Length > 0)
                {
                    foreach(AppleInAppPurchaseReceipt receipt in appleReceipt.inAppPurchaseReceipts)
                    {
                        switch(receipt.productType)
                        {
                            case 0: //NonConsumable
                            case 2: //Non-Renewing Subscription
                                if (receipt.cancellationDate == DateTime.MinValue) hasReceipt = true;
                                break;
                            case 3: //Auto-Renewing Subscription
                                if (receipt.subscriptionExpirationDate != DateTime.MinValue) hasReceipt = true;
                                break;
                        }

                        if (hasReceipt == true) break;
                    }
                }
            #endif

            return hasReceipt;
        }


        PurchaseState LocalValidation(Product product)
        {
            try
            {
                IPurchaseReceipt[] result = localValidator.Validate(product.receipt);

                foreach (IPurchaseReceipt receipt in result)
                {
                    if (receipt is GooglePlayReceipt googleReceipt)
                    {
                        if ((int)googleReceipt.purchaseState == 2 || (int)googleReceipt.purchaseState == 4)
                        {
                            //deferred IAP, payment not processed yet
                            return PurchaseState.Pending;
                        }
                    }
                }

                return PurchaseState.Purchased;
            }
            //if the purchase is deemed invalid, the validator throws an exception
            catch (IAPSecurityException)
            {
                return PurchaseState.Failed;
            }
        }


        string GetType(ProductType type)
        {
            switch (type)
            {
                case ProductType.Consumable:
                case ProductType.Subscription:
                    return type.ToString();

                default:
                    return "Non-Consumable";
            }
        }


        static bool IsLocalValidationSupported()
        {
            AppStore currentAppStore = StandardPurchasingModule.Instance().appStore;

            //The CrossPlatform validator only supports the GooglePlayStore and Apple's App Stores.
            if (currentAppStore == AppStore.GooglePlay ||
               currentAppStore == AppStore.AppleAppStore || currentAppStore == AppStore.MacAppStore)
                return true;

            return false;
        }


        static bool IsServerValidationSupported()
        {
            AppStore currentAppStore = StandardPurchasingModule.Instance().appStore;

            //The Receipt Validator server only supports the GooglePlayStore and Apple's App Stores.
            if (currentAppStore == AppStore.GooglePlay ||
               currentAppStore == AppStore.AppleAppStore || currentAppStore == AppStore.MacAppStore)
                return true;

            return false;
        }


        void OnDestroy()
        {
            if (_Instance == this)
            {
                _Instance = null;
            }
        }
    }


    /// <summary>
    /// Available options for fetching User Inventory.
    /// </summary>
    public enum InventoryRequestType
    {
        Disabled,
        Once,
        Delay
    }


    /// <summary>
    /// State of the purchase with local validation.
    /// </summary>
    public enum PurchaseState
    {
        Purchased,
        Pending,
        Failed
    }


    /// <summary>
    /// Parameters required for a server-side validation request.
    /// </summary>
    [System.Serializable]
    struct ReceiptRequest
    {
        public string store;
        public string bid;
        public string pid;
        public string type;
        public string user;
        public string receipt;
    }


    /// <summary>
    /// Response parameters received from a server-side validation request.
    /// </summary>
    [System.Serializable]
    public struct PurchaseResponse
    {
        public int status;
        public string type;
        public long expiresDate;
        public bool autoRenew;
        public bool billingRetry;
        public string productId;
        public bool sandbox;

        public override string ToString()
        {
            return $"ProductId:{productId}, Status:{status}, Type:{type}, ExpiresDate:{expiresDate}, AutoRenew:{autoRenew}, BillingRetry:{billingRetry}, Sandbox:{sandbox}";
        }
    }
}