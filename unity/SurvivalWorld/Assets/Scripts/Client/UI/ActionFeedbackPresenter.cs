using UnityEngine;
using UnityEngine.UI;

namespace SurvivalWorld.Client.UI
{
    public sealed class ActionFeedbackPresenter : MonoBehaviour
    {
        [SerializeField] private Text messageText;
        [SerializeField] private float visibleSeconds = 2f;
        private float hideAtTime;

        public static ActionFeedbackPresenter GetOrCreate()
        {
            ActionFeedbackPresenter existing = FindFirstObjectByType<ActionFeedbackPresenter>();
            if (existing != null) return existing;

            var canvasObject = new GameObject("PlaytestActionFeedbackCanvas");
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 22;
            canvasObject.AddComponent<CanvasScaler>();
            canvasObject.AddComponent<GraphicRaycaster>();

            GameObject textObject = new GameObject("FeedbackText");
            textObject.transform.SetParent(canvasObject.transform, false);
            Text text = textObject.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 18;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            RectTransform rect = text.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -72f);
            rect.sizeDelta = new Vector2(680f, 44f);

            ActionFeedbackPresenter presenter = canvasObject.AddComponent<ActionFeedbackPresenter>();
            presenter.messageText = text;
            text.enabled = false;
            return presenter;
        }

        private void Update()
        {
            if (messageText != null && messageText.enabled && Time.time >= hideAtTime)
            {
                messageText.enabled = false;
            }
        }

        public void Show(string message)
        {
            if (messageText == null) return;
            messageText.text = message ?? string.Empty;
            messageText.enabled = true;
            hideAtTime = Time.time + Mathf.Max(0.1f, visibleSeconds);
        }
    }
}