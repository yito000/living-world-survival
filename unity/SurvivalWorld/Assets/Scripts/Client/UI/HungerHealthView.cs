using UnityEngine;
using UnityEngine.UI;

namespace SurvivalWorld.Client.UI
{
    public sealed class HungerHealthView : MonoBehaviour
    {
        [SerializeField] private Slider hungerSlider;
        [SerializeField] private Slider healthSlider;
        [SerializeField] private Text hungerText;
        [SerializeField] private Text healthText;

        public void SetVitals(float hunger, float health)
        {
            SetSlider(hungerSlider, hunger);
            SetSlider(healthSlider, health);
            if (hungerText != null)
            {
                hungerText.text = Mathf.RoundToInt(hunger).ToString();
            }

            if (healthText != null)
            {
                healthText.text = Mathf.RoundToInt(health).ToString();
            }
        }

        private static void SetSlider(Slider slider, float value)
        {
            if (slider != null)
            {
                slider.minValue = 0f;
                slider.maxValue = 100f;
                slider.value = Mathf.Clamp(value, 0f, 100f);
            }
        }
    }
}
