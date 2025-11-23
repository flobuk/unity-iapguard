using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using SimpleJSON;

namespace FLOBUK.IAPGUARD
{
    using UnityEngine.Purchasing;
    using UnityEngine.Purchasing.Security;

    /// <summary>
    /// IAPGUARD SDK. Integrate with your Unity IAP manager.
    /// Does local and then server validation for your in-app purchases.
    /// </summary>
    public class IAPGuard : MonoBehaviour
    {
        /// <summary>
        /// Reference to this manager instance.
        /// </summary>
        public static IAPGuard Instance { get; private set; }
        
        /// <summary>
        /// Callback from receipt validation request.
        /// bool = success true/false, Product = requested Product, JSONNode = raw server JSON
        /// </summary>
        public static event Action<bool, Product, JSONNode> validationCallback;

        /// <summary>
        /// Callback from user inventory request.
        /// string = Product Id, PurchaseResponse = server data
        /// </summary>
        public static event Action<Dictionary<string, PurchaseResponse>> inventoryCallback;

        private const string validationEndpoint = "https://api.iapguard.com/v1/receipt/";
        private const string inventoryEndpoint = "https://api.iapguard.com/v1/user/";
        private const string lastInventoryTimestampKey = "fbrv_inventory_timestamp";

        [Header("General Data")]
        [Tooltip("The 16-character application ID from the IAPGUARD dashboard.")]
        public string appID;
        [Tooltip("User identifier set from your Authentication system when using User Inventory.")]
        public string userID;

        [Header("User Inventory is not supported on the Free plan.", order = 0)]
        [Header("Please leave it on 'Disabled' if you didn't upgrade.", order = 1)]
        [Header("Inventory", order = 2)]
        public InventoryRequestType inventoryRequestType = InventoryRequestType.Disabled;

        private Dictionary<string, PurchaseResponse> inventory = new Dictionary<string, PurchaseResponse>();
        private CrossPlatformValidator localValidator = null;
        private StoreController controller;
        private float lastInventoryTime = -1;
        private bool inventoryRequestActive = false;
        private int inventoryDelay = 1800;


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
        }


        /// <summary>
        /// Initialize the component by passing in a reference to Unity IAP.
        /// </summary>
        public void Initialize(StoreController controller)
        {
            this.controller = controller;
            controller.OnPurchasesFetched += _ => RequestInventory();

            if (IsLocalValidationSupported())
            {
                #if !UNITY_EDITOR
                    localValidator = new CrossPlatformValidator(GooglePlayTangle.Data(), Application.identifier);
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
                    Debug.LogWarning("IAPGUARD: CanRequestInventory returned false.");

                return;
            }

            //server validation is not supported on this platform, so no inventory is stored either or requests exceeded
            if (controller == null || !IsServerValidationSupported())
            {
                if (Debug.isDebugBuild)
                    Debug.LogWarning("IAPGUARD: Inventory Request not supported.");

                return;
            }

            //no purchase detected on this account, RequestInventory call is not necessary and was cancelled
            //if you are sure that this account has purchased products, instruct the user to initiate a restore first
            if (!HasPurchaseActive() && !HasPurchaseHistory())
            {
                if (Debug.isDebugBuild)
                    Debug.LogWarning("IAPGUARD: Inventory Request not necessary.");

                return;
            }

            inventoryRequestActive = true;
            StartCoroutine(RequestInventoryRoutine());
        }


        //actual inventory HTTP routine
        private IEnumerator RequestInventoryRoutine()
        {
            using (UnityWebRequest www = UnityWebRequest.Get(inventoryEndpoint + appID + "/" + userID))
            {
                www.SetRequestHeader("content-type", "application/json");
                yield return www.SendWebRequest();

                //raw JSON response
                JSONNode rawResponse = JSON.Parse(www.downloadHandler.text);
                JSONArray purchaseArray = rawResponse["purchases"].AsArray;

                //populate dictionary with server PurchaseResponses
                inventory.Clear();
                for (int i = 0; i < purchaseArray.Count; i++)
                {
                    inventory.Add(purchaseArray[i]["data"]["productId"].Value, JsonUtility.FromJson<PurchaseResponse>(purchaseArray[i]["data"].ToString()));
                }

                SetPurchaseHistory();
            }

            lastInventoryTime = Time.realtimeSinceStartup;
            inventoryRequestActive = false;
            inventoryCallback?.Invoke(inventory);
        }


        /// <summary>
        /// Request validation of a newly bought or restored product receipt.
        /// </summary>
        public PurchaseState RequestPurchase(PendingOrder order)
        {
            //assume that the purchase is valid as default
            PurchaseState state = PurchaseState.Purchased;
            //running on unsupported store
            if (controller == null)
            {
                return state;
            }

            //nothing to validate without receipt            
            if (string.IsNullOrEmpty(order.Info.TransactionID))
            {
                return PurchaseState.Failed;
            }

            //if local validation is supported it could return otherwise
            if (IsLocalValidationSupported() && localValidator != null)
            {
                state = LocalValidation(order);
            }

            //local validation was not supported or it passed as valid
            //now do the server validation, if supported and keep transaction pending
            if (state == PurchaseState.Purchased && IsServerValidationSupported())
            {
                StartCoroutine(RequestPurchaseRoutine(order));
                return PurchaseState.Pending;
            }

            //return state of local validation, or default state
            //if no validation technique was supported at all
            return state;
        }


        //actual receipt validation HTTP routine
        private IEnumerator RequestPurchaseRoutine(Order order)
        {
            //if the app is closed during this time, ProcessPurchase will be
            //called again for the same purchase once the app is opened again
            Product product = order.CartOrdered.Items().First().Product;

            ReceiptRequest request = new ReceiptRequest()
            {
                store = DefaultStoreHelper.GetDefaultStoreName(),
                bid = Application.identifier,
                pid = product.definition.storeSpecificId,
                user = userID,
                type = GetType(product.definition.type),
                receipt = order.Info.TransactionID
            };

            string postData = JsonUtility.ToJson(request);
            JSONNode rawResponse = null;
            bool success = false;

            using (UnityWebRequest www = UnityWebRequest.Post(validationEndpoint + appID, postData, "application/json"))
            {
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
                    if (Debug.isDebugBuild)
                        Debug.LogWarning("IAPGUARD Validation failed for: '" + product.definition.storeSpecificId + "'.\n" + www.downloadHandler.text);
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

                validationCallback?.Invoke(success, product, rawResponse);
            }

            //do not complete pending purchases but still leave them open for processing again later
            if (rawResponse == null || rawResponse.HasKey("error") && rawResponse["code"] == 10130)
            {
                yield break;
            }

            //refresh reference as according to Unity they become stale after async delays
            Order currentOrder = controller.GetPurchases().FirstOrDefault(p => p.CartOrdered.Items().First().Product.definition.id == product.definition.id);

            //once we have done the validation in our backend, we confirm the order status
            if (currentOrder != null && currentOrder is PendingOrder)
                controller.ConfirmPurchase(currentOrder as PendingOrder);
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


        //actual restore HTTP routine
        //internally this does a purchase validation with existing receipts
        private IEnumerator RequestRestoreRoutine()
        {
            foreach (Order order in controller.GetPurchases())
            {
                Product product = order.CartOrdered.Items().First().Product;

                if (product.definition.type == ProductType.Consumable || string.IsNullOrEmpty(order.Info.TransactionID) || inventory.ContainsKey(product.definition.id))
                    continue;

                StartCoroutine(RequestPurchaseRoutine(order));
                yield return new WaitForSecondsRealtime(UnityEngine.Random.Range(2f, 5f));
            }
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

        
        /// <summary>
        /// Return current user inventory stored in memory.
        /// </summary>
        public Dictionary<string, PurchaseResponse> GetInventory()
        {
            return inventory;
        }


        /// <summary>
        /// Return whether a product is included in user inventory received earlier.
        /// Otherwise returns false on the Free plan or with inventory disabled since there is no inventory.
        /// </summary>
        public bool IsOwned(string productId)
        {
            int[] purchaseStates = new int[] { 0, 1, 4 };
            if (inventoryRequestType != InventoryRequestType.Disabled && inventory.ContainsKey(productId) && Array.Exists(purchaseStates, x => x == inventory[productId].status))
            {
                return true;
            }

            return false;
        }


        //saves a PlayerPref if the last user inventory response contained values
        private void SetPurchaseHistory()
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


        //check whether inventory requests returned values within the past month
        private bool HasPurchaseHistory()
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


        //check whether Unity IAP returned purchases from the App Store
        private bool HasPurchaseActive()
        {
            return controller.GetPurchases().Count > 0;
        }


        //does local validation for receipt format on Google Play
        private PurchaseState LocalValidation(Order order)
        {
            try
            {
                IPurchaseReceipt[] result = localValidator.Validate(order.Info.Receipt);

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
            //if the receipt is deemed invalid, the validator throws an exception
            catch (IAPSecurityException)
            {
                return PurchaseState.Failed;
            }
        }


        //format ProductType to expected string
        private string GetType(ProductType type)
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


        //check whether product receipts can be validated locally
        private bool IsLocalValidationSupported()
        {
            //The CrossPlatform validator only supports the GooglePlayStore.
            if (Application.platform == RuntimePlatform.Android && DefaultStoreHelper.GetDefaultStoreName() == GooglePlay.Name)
                return true;

            return false;
        }


        //check whether product receipts can be validated on the IAPGUARD platform
        private bool IsServerValidationSupported()
        {
            string currentAppStore = DefaultStoreHelper.GetDefaultStoreName();

            //This SDK only supports receipt validation on Google Play and the Apple App Store.
            return Application.platform == RuntimePlatform.Android && currentAppStore == GooglePlay.Name ||
                   Application.platform == RuntimePlatform.IPhonePlayer ||
                   Application.platform == RuntimePlatform.OSXPlayer ||
                   Application.platform == RuntimePlatform.tvOS;
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
    /// State of the purchase after local validation.
    /// </summary>
    public enum PurchaseState
    {
        Purchased,
        Pending,
        Failed
    }


    /// <summary>
    /// Parameters required for a server-side validation request.
    /// See https://docs.iapguard.com/api/rest#validate-receipt
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
    /// See https://docs.iapguard.com/api/rest#validate-receipt
    /// </summary>
    [System.Serializable]
    public struct PurchaseResponse
    {
        public int status;
        public string type;
        public long? expiresDate;
        public bool? autoRenew;
        public int? cancelReason;
        public bool? billingRetry;
        public string productId;
        public string groupId;
        public bool sandbox;

        public override string ToString()
        {
            string result = $"ProductId:{productId}, Status:{status}, Type:{type}, Sandbox:{sandbox}";

            if (expiresDate.HasValue) result += $", ExpiresDate:{expiresDate.Value}";
            if (autoRenew.HasValue) result += $", AutoRenew:{autoRenew.Value}";
            if (cancelReason.HasValue) result += $", CancelReason:{cancelReason.Value}";
            if (billingRetry.HasValue) result += $", BillingRetry:{billingRetry.Value}";
            if (!string.IsNullOrEmpty(groupId)) result += $", GroupId:{groupId}";

            return result;
        }
    }
}