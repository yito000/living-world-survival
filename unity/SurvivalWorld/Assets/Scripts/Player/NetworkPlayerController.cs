using System;
using System.Globalization;
using FishNet.Connection;
using FishNet.Object;
using Survival.V1;
using UnityEngine;

namespace SurvivalWorld.Player
{
    [RequireComponent(typeof(CharacterController))]
    public sealed class NetworkPlayerController : NetworkBehaviour
    {
        [SerializeField] private ThirdPersonInputReader inputReader;
        [SerializeField] private float walkSpeed = 4.5f;
        [SerializeField] private float sprintSpeed = 7f;
        [SerializeField] private float jumpVelocity = 5f;
        [SerializeField] private float gravity = -18f;
        [SerializeField] private float rotationSpeed = 18f;

        private CharacterController characterController;
        private float verticalVelocity;
        private bool commandLineTestDrive;
        private Vector2 commandLineTestMove;
        private bool loggedServerMovement;

        private void Awake()
        {
            characterController = GetComponent<CharacterController>();
            if (inputReader == null)
            {
                inputReader = GetComponent<ThirdPersonInputReader>();
            }

            ConfigureCommandLineTestDrive();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            TryBindOwnerCamera();
        }

        public override void OnOwnershipClient(NetworkConnection prevOwner)
        {
            base.OnOwnershipClient(prevOwner);
            TryBindOwnerCamera();
        }

        private void Update()
        {
            if (NetworkObject == null || !NetworkObject.IsSpawned || !IsOwner || inputReader == null)
            {
                return;
            }

            float yaw = Camera.main == null ? transform.eulerAngles.y : Camera.main.transform.eulerAngles.y;
            long tick = TimeManager == null ? Time.frameCount : TimeManager.LocalTick;
            InputCommand command = commandLineTestDrive
                ? inputReader.BuildCommand(commandLineTestMove, Vector2.zero, false, false, yaw, tick)
                : inputReader.ReadCurrentCommand(tick, yaw);
            SubmitInputServerRpc(command.Tick, command.Sequence, command.Move.X, command.Move.Y, command.Move.Z, command.Look.X, command.Look.Y, command.Look.Z, command.Jump, command.Sprint);

            if (!IsServerStarted)
            {
                ApplyMovement(command, Time.deltaTime);
            }
        }

        [ServerRpc]
        private void SubmitInputServerRpc(long tick, long sequence, float moveX, float moveY, float moveZ, float lookX, float lookY, float lookZ, bool jump, bool sprint)
        {
            var command = new InputCommand
            {
                Tick = tick,
                Sequence = sequence,
                Move = new Vec3 { X = moveX, Y = moveY, Z = moveZ },
                Look = new Vec3 { X = lookX, Y = lookY, Z = lookZ },
                Jump = jump,
                Sprint = sprint
            };

            ApplyMovement(command, Time.deltaTime);
            if (!loggedServerMovement && (moveX * moveX + moveZ * moveZ) > 0.0001f)
            {
                loggedServerMovement = true;
                Debug.Log($"Server received movement input for {name}: sequence={sequence}, position={transform.position}.");
            }

            ApplyAuthoritativeStateObserversRpc(transform.position, transform.rotation);
        }

        [ObserversRpc(BufferLast = true)]
        private void ApplyAuthoritativeStateObserversRpc(Vector3 position, Quaternion rotation)
        {
            if (IsServerStarted)
            {
                return;
            }

            transform.SetPositionAndRotation(position, rotation);
        }

        private void TryBindOwnerCamera()
        {
            if (!IsOwner || Camera.main == null)
            {
                return;
            }

            ThirdPersonCameraRig cameraRig = Camera.main.GetComponent<ThirdPersonCameraRig>();
            if (cameraRig != null)
            {
                cameraRig.Target = transform;
            }
        }

        private void ConfigureCommandLineTestDrive()
        {
            if (!HasCommandLineArg("--sw-test-drive"))
            {
                return;
            }

            commandLineTestDrive = true;
            float moveX = GetCommandLineFloat("--sw-test-move-x", 0f);
            float moveZ = GetCommandLineFloat("--sw-test-move-z", 1f);
            commandLineTestMove = new Vector2(moveX, moveZ);
        }

        private static bool HasCommandLineArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static float GetCommandLineFloat(string name, float fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal) &&
                    float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                {
                    return value;
                }
            }

            return fallback;
        }

        private void ApplyMovement(InputCommand command, float deltaTime)
        {
            Vector3 move = new Vector3(command.Move.X, 0f, command.Move.Z);
            if (move.sqrMagnitude > 1f)
            {
                move.Normalize();
            }

            bool grounded = characterController == null || characterController.isGrounded;
            if (grounded && verticalVelocity < 0f)
            {
                verticalVelocity = -1f;
            }

            if (command.Jump && grounded)
            {
                verticalVelocity = jumpVelocity;
            }

            verticalVelocity += gravity * deltaTime;
            float speed = command.Sprint ? sprintSpeed : walkSpeed;
            Vector3 displacement = move * speed;
            displacement.y = verticalVelocity;

            if (characterController != null && characterController.enabled)
            {
                characterController.Move(displacement * deltaTime);
            }
            else
            {
                transform.position += displacement * deltaTime;
            }

            if (move.sqrMagnitude > 0.0001f)
            {
                Quaternion target = Quaternion.LookRotation(move, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-rotationSpeed * deltaTime));
            }
        }
    }
}