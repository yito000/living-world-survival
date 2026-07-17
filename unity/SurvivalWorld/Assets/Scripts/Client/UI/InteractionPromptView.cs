using SurvivalWorld.Client.Interaction;
using UnityEngine;
using UnityEngine.UI;

namespace SurvivalWorld.Client.UI
{
    public sealed class InteractionPromptView : MonoBehaviour
    {
        [SerializeField] private Text promptText;

        public static InteractionPromptView GetOrCreate()
        {
            InteractionPromptView existing = FindFirstObjectByType<InteractionPromptView>();
            if (existing != null) return existing;

            var canvasObject = new GameObject("PlaytestInteractionPromptCanvas");
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 20;
            canvasObject.AddComponent<CanvasScaler>();
            canvasObject.AddComponent<GraphicRaycaster>();

            GameObject textObject = new GameObject("PromptText");
            textObject.transform.SetParent(canvasObject.transform, false);
            Text text = textObject.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 22;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            RectTransform rect = text.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, -80f);
            rect.sizeDelta = new Vector2(520f, 40f);

            InteractionPromptView view = canvasObject.AddComponent<InteractionPromptView>();
            view.promptText = text;
            view.Hide();
            return view;
        }

        public void SetCandidate(InteractionCandidate candidate)
        {
            if (promptText == null) return;
            promptText.text = candidate.PromptText;
            promptText.enabled = candidate.IsValid;
        }

        public void Hide()
        {
            if (promptText != null) promptText.enabled = false;
        }
    }
}