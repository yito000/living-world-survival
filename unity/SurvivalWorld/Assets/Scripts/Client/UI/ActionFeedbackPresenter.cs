using UnityEngine;
using UnityEngine.UI;

namespace SurvivalWorld.Client.UI
{
    public sealed class ActionFeedbackPresenter : MonoBehaviour
    {
        [SerializeField] private Text messageText;
        [SerializeField] private float visibleSeconds = 2f;
        private float hideAtTime;

        private void Update()
        {
            if (messageText != null && messageText.enabled && Time.time >= hideAtTime)
            {
                messageText.enabled = false;
            }
        }

        public void Show(string message)
        {
            if (messageText == null)
            {
                return;
            }

            messageText.text = message ?? string.Empty;
            messageText.enabled = true;
            hideAtTime = Time.time + Mathf.Max(0.1f, visibleSeconds);
        }
    }
}
