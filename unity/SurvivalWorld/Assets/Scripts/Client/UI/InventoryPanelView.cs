using System.Text;
using SurvivalWorld.Inventory;
using UnityEngine;
using UnityEngine.UI;

namespace SurvivalWorld.Client.UI
{
    public sealed class InventoryPanelView : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Text contentText;
        private readonly InventoryViewModel viewModel = new InventoryViewModel();

        public static InventoryPanelView GetOrCreate()
        {
            InventoryPanelView existing = FindFirstObjectByType<InventoryPanelView>();
            if (existing != null) return existing;

            var canvasObject = new GameObject("PlaytestInventoryCanvas");
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 12;
            canvasObject.AddComponent<CanvasScaler>();
            canvasObject.AddComponent<GraphicRaycaster>();

            GameObject panel = new GameObject("InventoryPanel");
            panel.transform.SetParent(canvasObject.transform, false);
            panel.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.72f);
            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(1f, 0.5f);
            panelRect.anchorMax = new Vector2(1f, 0.5f);
            panelRect.pivot = new Vector2(1f, 0.5f);
            panelRect.anchoredPosition = new Vector2(-24f, 0f);
            panelRect.sizeDelta = new Vector2(360f, 420f);

            GameObject textObject = new GameObject("InventoryText");
            textObject.transform.SetParent(panel.transform, false);
            Text text = textObject.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 16;
            text.alignment = TextAnchor.UpperLeft;
            text.color = Color.white;
            RectTransform textRect = text.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(16f, 16f);
            textRect.offsetMax = new Vector2(-16f, -16f);

            InventoryPanelView view = canvasObject.AddComponent<InventoryPanelView>();
            view.panelRoot = panel;
            view.contentText = text;
            view.SetVisible(false);
            return view;
        }

        public void Toggle() => SetVisible(panelRoot == null || !panelRoot.activeSelf);

        public void SetVisible(bool visible)
        {
            if (panelRoot != null) panelRoot.SetActive(visible);
        }

        public void ApplySnapshot(InventorySnapshot snapshot)
        {
            viewModel.ApplySnapshot(snapshot);
            if (contentText == null) return;
            var builder = new StringBuilder();
            builder.AppendLine("Inventory");
            if (snapshot == null) builder.AppendLine("No snapshot");
            else
            {
                builder.AppendLine("Version " + snapshot.Version);
                foreach (InventorySlotViewState slot in viewModel.Slots)
                {
                    builder.AppendLine(slot.SlotIndex + ": " + slot.ItemDefinitionId + " x" + slot.Quantity + " reserved " + slot.Reserved);
                }
            }
            contentText.text = builder.ToString();
        }

        public void SetStatus(string status)
        {
            if (contentText != null) contentText.text = string.IsNullOrWhiteSpace(status) ? "Inventory" : status;
        }
    }
}