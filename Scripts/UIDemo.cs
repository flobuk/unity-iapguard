using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using SimpleJSON;

namespace FLOBUK.IAPGUARD.Demo
{
    /// <summary>
    /// Displays Server and Validation responses.
    /// </summary>
    public class UIDemo : MonoBehaviour
    {
        //display of purchased state or amount
        [Header("Purchased Flags")]
        public GameObject NonConsumableFlag;
        public GameObject SubscriptionFlag;

        //display of system and server messages
        public ScrollRect LogScroll;
        public Text LogText;
        //notice when running on non-mobile platforms
        public GameObject EditorText;
        //feedback window
        public GameObject InfoWindow;
        public Text InfoText;

        int processingPurchasesCount;

        private IAPManager instance;


        void Start()
        {
            #if !UNITY_EDITOR
                EditorText.SetActive(false);
            #endif

            //get instance
            instance = IAPManager.GetInstance();
            if (!instance) return;

            //subscribe to callbacks
            IAPGuard.inventoryCallback += InventoryRetrieved;
            IAPManager.purchaseCallback += PurchaseResult;
            IAPManager.debugCallback += PrintMessage;

            UpdateUI();
        }


        //IAPGuard.inventoryCallback
        void InventoryRetrieved()
        {
            PrintMessage(Color.green, "Inventory retrieved.");

            Dictionary<string, PurchaseResponse> inventory = IAPGuard.Instance.GetInventory();
            foreach (string productID in inventory.Keys)
                PrintMessage(Color.white, productID + ": " + inventory[productID].ToString());

            UpdateUI();
        }


        //buy buttons for different product types
        public void BuyConsumable() { Buy(instance.consumableProductId); }
        public void BuyNonconsumable() { Buy(instance.nonconsumableProductId); }
        public void BuySubscription() { Buy(instance.subscriptionProductId); }


        //buy method triggering Unity IAP
        void Buy(string productId)
        {
            processingPurchasesCount++;
            PrintMessage(Color.white, "Purchase Processing Count: " + processingPurchasesCount);
            UpdateUI();

            instance.controller.InitiatePurchase(productId);
        }


        //trigger sending receipts to the IAPGUARD backend again
        public void RestoreTransactions()
        {
            IAPManager.GetInstance().RestoreTransactions();
        }


        //IAPManagerDemo.purchaseCallback
        //result is JSONNode or null
        void PurchaseResult(bool success, JSONNode result)
        {
            processingPurchasesCount--;
            processingPurchasesCount = Mathf.Clamp(processingPurchasesCount, 0, int.MaxValue);

            //Log output
            switch (success)
            {
                case true:
                    PrintMessage(Color.green, "Purchase validation success!");
                    break;

                case false:
                    PrintMessage(Color.red, "Purchase validation failed.");
                    break;
            }

            //UI feedback window
            InfoWindow.SetActive(true);

            if (result != null)
            {
                PrintMessage(Color.white, "Raw: " + result.ToString());

                InfoText.text = "Product purchase: " + result["data"]["productId"];
                InfoText.text += "\n" + "Purchase result: " + success;
                InfoText.text += "\n\n" + "See Log for more information!";
            }
            else
                InfoText.text = "Purchase cancelled.";

            PrintMessage(Color.white, "Purchase Processing Count: " + processingPurchasesCount);
            UpdateUI();
        }


        //message display
        void PrintMessage(Color color, string text)
        {
            LogText.text += "\n\n" + "<color=#" + ColorUtility.ToHtmlStringRGB(color) + ">" + text + "</color>";
        }


        //set log scroll rect to bottom
        public void ForceScrollDown()
        {
            StartCoroutine(ForceScrollDownRoutine());
        }


        IEnumerator ForceScrollDownRoutine()
        {
            yield return new WaitForEndOfFrame();
            LogScroll.verticalNormalizedPosition = 0f;
            Canvas.ForceUpdateCanvases();
        }


        //try to get inventory
        public void GetInventory()
        {
            if(!IAPGuard.Instance.CanRequestInventory())
            {
                PrintMessage(Color.red, "Inventory call is not possible. If you are on a paid plan, check your selected Inventory Request Type.");
                return;
            }

            IAPGuard.Instance.RequestInventory();
        }


        //update graphical display of text contents with current states
        void UpdateUI()
        {
            NonConsumableFlag.SetActive(IAPGuard.Instance.IsPurchased(instance.nonconsumableProductId));
            SubscriptionFlag.SetActive(IAPGuard.Instance.IsPurchased(instance.subscriptionProductId));
        }
    }
}
