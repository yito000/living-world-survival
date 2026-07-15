using UnityEngine;
using UnityEngine.UI;

namespace SurvivalWorld.Client.UI
{
    public sealed class StationJobView : MonoBehaviour
    {
        [SerializeField] private Slider progressSlider;
        [SerializeField] private Button cancelButton;

        public Button CancelButton => cancelButton;

        public void SetProgress(long startedAtUnixMs, long completeAtUnixMs, long nowUnixMs)
        {
            if (progressSlider == null)
            {
                return;
            }

            long duration = completeAtUnixMs - startedAtUnixMs;
            if (duration <= 0)
            {
                progressSlider.value = 1f;
                return;
            }

            progressSlider.value = Mathf.Clamp01((nowUnixMs - startedAtUnixMs) / (float)duration);
        }
    }
}
