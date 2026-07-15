using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SurvivalWorld.Net
{
    public sealed class NetworkSessionClient : MonoBehaviour
    {
        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private string worldSceneName = "World_MVP";

        private string pendingJoinTicket;
        private UniTaskCompletionSource authenticatedCompletion;

        public async UniTask ConnectWithJoinTicketAsync(string serverEndpoint, string joinTicket, CancellationToken cancellationToken)
        {
            if (authenticatedCompletion != null)
            {
                throw new InvalidOperationException("A FishNet connection attempt is already in progress.");
            }

            if (networkManager == null)
            {
                networkManager = FindFirstObjectByType<NetworkManager>();
            }

            if (networkManager == null)
            {
                throw new InvalidOperationException("NetworkManager is required to connect.");
            }

            if (!EndpointParser.TryParse(serverEndpoint, out var endpoint))
            {
                throw new ArgumentException("Invalid server endpoint: " + serverEndpoint, nameof(serverEndpoint));
            }

            await EnsureWorldSceneLoadedBeforeConnectAsync(cancellationToken);
            if (networkManager == null)
            {
                networkManager = FindFirstObjectByType<NetworkManager>();
            }

            if (networkManager == null)
            {
                throw new InvalidOperationException("NetworkManager is required after loading the world scene.");
            }

            Debug.Log("Starting FishNet client connection to " + endpoint.Host + ":" + endpoint.Port + ".");
            pendingJoinTicket = joinTicket;
            authenticatedCompletion = new UniTaskCompletionSource();
            networkManager.ClientManager.OnClientConnectionState += OnClientConnectionState;
            networkManager.ClientManager.OnAuthenticated += OnAuthenticated;

            try
            {
                bool started = networkManager.ClientManager.StartConnection(endpoint.Host, endpoint.Port);
                if (!started)
                {
                    throw new InvalidOperationException("FishNet client failed to start.");
                }

                using (cancellationToken.Register(() => authenticatedCompletion.TrySetCanceled(cancellationToken)))
                {
                    await authenticatedCompletion.Task;
                }
            }
            finally
            {
                CleanupHandlers();
                pendingJoinTicket = null;
                authenticatedCompletion = null;
            }
        }

        private async UniTask EnsureWorldSceneLoadedBeforeConnectAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(worldSceneName))
            {
                return;
            }

            Scene loadedScene = SceneManager.GetSceneByName(worldSceneName);
            if (loadedScene.IsValid() && loadedScene.isLoaded)
            {
                if (SceneManager.GetActiveScene().name != worldSceneName)
                {
                    SceneManager.SetActiveScene(loadedScene);
                }

                return;
            }

            AsyncOperation operation = SceneManager.LoadSceneAsync(worldSceneName, LoadSceneMode.Single);
            if (operation == null)
            {
                throw new InvalidOperationException("Failed to load client world scene: " + worldSceneName);
            }

            await operation.ToUniTask(cancellationToken: cancellationToken);
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started && !string.IsNullOrWhiteSpace(pendingJoinTicket))
            {
                SendJoinTicketAfterHandshakeAsync(destroyCancellationToken).Forget();
            }
            else if (args.ConnectionState == LocalConnectionState.Stopped)
            {
                authenticatedCompletion?.TrySetException(new InvalidOperationException("FishNet client disconnected before authentication. Check DS logs for join ticket rejection and verify the client endpoint is reachable over UDP."));
            }
        }

        private async UniTaskVoid SendJoinTicketAfterHandshakeAsync(CancellationToken cancellationToken)
        {
            await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
            if (string.IsNullOrWhiteSpace(pendingJoinTicket))
            {
                return;
            }

            Debug.Log("Sending join ticket broadcast to FishNet server.");
            networkManager.ClientManager.Broadcast(new JoinTicketBroadcast { Ticket = pendingJoinTicket });
        }

        private void OnAuthenticated()
        {
            Debug.Log("FishNet client authenticated.");
            authenticatedCompletion?.TrySetResult();
        }

        private void OnDestroy()
        {
            CleanupHandlers();
        }

        private void CleanupHandlers()
        {
            if (networkManager == null)
            {
                return;
            }

            networkManager.ClientManager.OnClientConnectionState -= OnClientConnectionState;
            networkManager.ClientManager.OnAuthenticated -= OnAuthenticated;
        }
    }
}
