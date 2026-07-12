using UnityEngine;

namespace SurvivalWorld.Player
{
    public sealed class ThirdPersonCameraRig : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private Vector3 shoulderOffset = new Vector3(0.6f, 1.6f, -3.5f);
        [SerializeField] private float lookSensitivity = 0.08f;
        [SerializeField] private float minPitch = -35f;
        [SerializeField] private float maxPitch = 65f;
        [SerializeField] private LayerMask collisionMask = ~0;
        [SerializeField] private float collisionRadius = 0.2f;

        private float yaw;
        private float pitch = 15f;

        public float Yaw => yaw;
        public Transform Target
        {
            get => target;
            set => target = value;
        }

        private void Awake()
        {
            if (Application.isBatchMode)
            {
                gameObject.SetActive(false);
                return;
            }

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void LateUpdate()
        {
            if (target == null)
            {
                return;
            }

            Vector2 mouse = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));
            yaw += mouse.x * lookSensitivity;
            pitch = Mathf.Clamp(pitch - mouse.y * lookSensitivity, minPitch, maxPitch);

            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 pivot = target.position + Vector3.up * shoulderOffset.y;
            Vector3 desired = pivot + rotation * new Vector3(shoulderOffset.x, 0f, shoulderOffset.z);

            if (Physics.SphereCast(pivot, collisionRadius, desired - pivot, out RaycastHit hit, Vector3.Distance(pivot, desired), collisionMask, QueryTriggerInteraction.Ignore))
            {
                desired = hit.point + hit.normal * collisionRadius;
            }

            transform.SetPositionAndRotation(desired, rotation);
        }
    }
}
