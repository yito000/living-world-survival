using UnityEngine;

namespace SurvivalWorld.Client.Interaction
{
    public sealed class InteractionScanner : MonoBehaviour
    {
        [SerializeField] private Camera sourceCamera;
        [SerializeField] private float range = 3f;
        [SerializeField] private LayerMask mask = ~0;

        public RaycastHit LastHit { get; private set; }
        public bool HasCandidate { get; private set; }

        private void Awake()
        {
            if (sourceCamera == null)
            {
                sourceCamera = Camera.main;
            }
        }

        private void Update()
        {
            Scan();
        }

        public bool Scan()
        {
            if (sourceCamera == null)
            {
                HasCandidate = false;
                return false;
            }

            Ray ray = sourceCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            HasCandidate = Physics.Raycast(ray, out RaycastHit hit, range, mask, QueryTriggerInteraction.Collide);
            LastHit = hit;
            return HasCandidate;
        }
    }
}
