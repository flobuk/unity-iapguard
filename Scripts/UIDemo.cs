using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using SimpleJSON;

namespace FLOBUK.IAPGUARD.Demo
{
    using UnityEngine.Purchasing;

    /// <summary>
    /// UI demo script for purchasing products and displaying server validation responses.
    /// </summary>
    public class UIDemo : MonoBehaviour
    {
        /// <summary>
        /// List of product flags that indicate when a product is owned.
        /// </summary>
        public List<ProductFlag> flags = new List<ProductFlag>();

        /// <summary>
        /// Log for system and server messages.
        /// </summary>
        public Text LogText;

        /// <summary>
        /// Notice when running on non-mobile platforms.
        /// </summary>
        public GameObject EditorText;

        /// <summary>
        /// Feedback window for the user.
        /// </summary>
        public GameObject InfoWindow;

        /// <summary>
        /// Text that is displayed in the Feedback window.
        /// </summary>
        public Text InfoText;


        void Start()
        {
            #if !UNITY_EDITOR
                EditorText.SetActive(false);
            #endif

            //required instances not available
            if (!IAPManager.Instance || !IAPGuard.Instance) return;

            //subscribe to callbacks
            IAPManager.debugCallback += PrintLog;
            IAPManager.initializeSucceededEvent += UpdatePurchased;
            IAPManager.Instance.controller.OnCheckEntitlement += OnCheckEntitlement;
            IAPManager.purchaseSucceededEvent += OnPurchaseSucceeded;
            IAPManager.purchaseFailedEvent += OnPurchaseFailed;
            IAPGuard.inventoryCallback += InventoryRetrieved;
        }


        /// <summary>
        /// Buy method triggering Unity IAP
        /// </summary>
        public void Buy(string productId)
        {
            IAPManager.Instance.Purchase(productId);
        }


        /// <summary>
        /// Trigger sending receipts to the IAPGUARD backend again
        /// </summary>
        public void RestoreTransactions()
        {
            IAPManager.Instance.RestoreTransactions();
        }


        //IAPGuard.inventoryCallback
        private void InventoryRetrieved(Dictionary<string, PurchaseResponse> inventory)
        {
            PrintLog(Color.green, "Inventory retrieved.");

            //do something with the inventory
            foreach (string productId in inventory.Keys)
            {
                PrintLog(Color.white, productId + ": " + inventory[productId].ToString());
            }

            //or query each product separately
            UpdatePurchased();
        }


        //IAPManager.purchaseSucceededEvent
        private void OnPurchaseSucceeded(Product product, JSONNode serverData, bool isNew)
        {
            if (serverData != null)
            {
                PrintLog(Color.green, "Purchase validation success!");
                PrintLog(Color.white, "Raw: " + serverData.ToString());
            }

            //show for new purchases, not for restores
            if (isNew == true)
            {
                //UI feedback window
                InfoText.text = "Product purchase: " + product.definition.id;
                InfoText.text += "\n" + "Purchase success";
                InfoWindow.SetActive(true);
            }

            SetPurchasedState(product.definition.id, true);
        }


        //IAPManager.purchaseFailedEvent
        private void OnPurchaseFailed(Product product, string error)
        {
            PrintLog(Color.red, "Purchase validation failed.");
            PrintLog(Color.white, "Raw: " + error);

            //UI feedback window
            InfoText.text = "Product purchase: " + (product != null ? product.definition.id : "Unknown");
            InfoText.text += "\n" + "Purchase failed: " + error;
            InfoWindow.SetActive(true);
        }


        //IAPManager.initializeSucceededEvent or manual
        //update product purchase flags in the UI
        private void UpdatePurchased()
        {
            for (int i = 0; i < flags.Count; i++)
            {
                bool? isOwned = IAPManager.Instance.IsPurchased(flags[i].id);
                if (isOwned.HasValue) SetPurchasedState(flags[i].id, isOwned.Value);
            }
        }


        //StoreController.OnCheckEntitlement
        private void OnCheckEntitlement(Entitlement entitlement)
        {
            //make sure we have a product
            Product product = entitlement.Product;
            if (product == null)
            {
                try
                { product = entitlement.Order.CartOrdered.Items().FirstOrDefault().Product; }
                catch (Exception)
                { }
            }

            //still not set
            if (product == null)
                return;

            SetPurchasedState(entitlement.Product.definition.id, entitlement.Status == EntitlementStatus.FullyEntitled);
        }


        //finds corresponding purchase flag and sets purchase state in UI
        private void SetPurchasedState(string productId, bool isPurchased)
        {
            for (int i = 0; i < flags.Count; i++)
            {
                if (flags[i].id == productId)
                {
                    flags[i].flag.SetActive(isPurchased);
                    break;
                }
            }
        }


        //log display
        private void PrintLog(Color color, string text)
        {
            LogText.text += "\n\n" + "<color=#" + ColorUtility.ToHtmlStringRGB(color) + ">" + text + "</color>";
        }


        //unsubscribe callbacks
        void OnDestroy()
        {
            IAPManager.debugCallback -= PrintLog;
            IAPManager.initializeSucceededEvent -= UpdatePurchased;
            IAPManager.Instance.controller.OnCheckEntitlement -= OnCheckEntitlement;
            IAPManager.purchaseSucceededEvent -= OnPurchaseSucceeded;
            IAPManager.purchaseFailedEvent -= OnPurchaseFailed;
            IAPGuard.inventoryCallback -= InventoryRetrieved;
        }
    }


    /// <summary>
    /// Mapping between product Id and UI owned flag.
    /// </summary>
    [System.Serializable]
    public class ProductFlag
    {
        public string id;

        public GameObject flag;
    }
}
