using System;
using System.Globalization;
using Survival.V1;
using SurvivalWorld.Player;
using UnityEngine;
using UnityEngine.UI;

namespace SurvivalWorld.Client.UI
{
    public sealed class BuyerStockView : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Text contentText;
        [SerializeField] private Button purchaseButton;
        private NetworkBuyerPurchaseCommandBridge bridge;
        private string buyerInstanceId = "mvp-buyer-stock";
        private string stockEntryId = "mvp-stock-cooked-meat";

        public static BuyerStockView GetOrCreate()
        {
            BuyerStockView existing = FindFirstObjectByType<BuyerStockView>();
            if (existing != null) return existing;
            var canvasObject = new GameObject("PlaytestBuyerCanvas");
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 14;
            canvasObject.AddComponent<CanvasScaler>();
            canvasObject.AddComponent<GraphicRaycaster>();

            GameObject panel = new GameObject("BuyerPanel");
            panel.transform.SetParent(canvasObject.transform, false);
            panel.AddComponent<Image>().color = new Color(0.02f, 0.04f, 0.06f, 0.82f);
            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 0.5f);
            panelRect.anchorMax = new Vector2(0f, 0.5f);
            panelRect.pivot = new Vector2(0f, 0.5f);
            panelRect.anchoredPosition = new Vector2(24f, 0f);
            panelRect.sizeDelta = new Vector2(340f, 220f);

            GameObject textObject = new GameObject("BuyerText");
            textObject.transform.SetParent(panel.transform, false);
            Text text = textObject.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 16;
            text.alignment = TextAnchor.UpperLeft;
            text.color = Color.white;
            RectTransform textRect = text.GetComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0f, 0.3f);
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(16f, 8f);
            textRect.offsetMax = new Vector2(-16f, -16f);

            GameObject buttonObject = new GameObject("PurchaseButton");
            buttonObject.transform.SetParent(panel.transform, false);
            buttonObject.AddComponent<Image>().color = new Color(0.18f, 0.32f, 0.52f, 1f);
            Button button = buttonObject.AddComponent<Button>();
            RectTransform buttonRect = buttonObject.GetComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(0f, 0f);
            buttonRect.anchorMax = new Vector2(1f, 0f);
            buttonRect.offsetMin = new Vector2(16f, 16f);
            buttonRect.offsetMax = new Vector2(-16f, 56f);

            GameObject labelObject = new GameObject("PurchaseButtonText");
            labelObject.transform.SetParent(buttonObject.transform, false);
            Text label = labelObject.AddComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 16;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.text = "Purchase";
            RectTransform labelRect = label.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            BuyerStockView view = canvasObject.AddComponent<BuyerStockView>();
            view.panelRoot = panel;
            view.contentText = text;
            view.purchaseButton = button;
            button.onClick.AddListener(view.SubmitPurchase);
            view.SetVisible(false);
            return view;
        }

        public void Open(NetworkBuyerPurchaseCommandBridge commandBridge, string buyerName, uint targetNetworkId)
        {
            bridge = commandBridge;
            buyerInstanceId = targetNetworkId == 0 ? "mvp-buyer-stock" : "buyer-" + targetNetworkId.ToString(CultureInfo.InvariantCulture);
            if (contentText != null) contentText.text = (string.IsNullOrWhiteSpace(buyerName) ? "Buyer" : buyerName) + "\nStock: cooked_meat\nPrice: server/API authoritative\nRemaining: server authoritative";
            SetVisible(true);
        }

        public void SetVisible(bool visible)
        {
            if (panelRoot != null) panelRoot.SetActive(visible);
        }

        private void SubmitPurchase()
        {
            if (bridge == null)
            {
                ActionFeedbackPresenter.GetOrCreate().Show("Buyer bridge missing");
                return;
            }

            bridge.SubmitPurchase(new BuyerPurchaseCommand
            {
                CommandId = "buyer-ui-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                BuyerInstanceId = buyerInstanceId,
                StockEntryId = stockEntryId,
                InventoryVersion = -1L
            });
        }
    }
}