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

                if (!string.IsNullOrWhiteSpace(worldSceneName) && SceneManager.GetActiveScene().name != worldSceneName)
                {
                    await SceneManager.LoadSceneAsync(worldSceneName).ToUniTask(cancellationToken: cancellationToken);
                }
            }
            finally
            {
                CleanupHandlers();
                pendingJoinTicket = null;
                authenticatedCompletion = null;
            }
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started && !string.IsNullOrWhiteSpace(pendingJoinTicket))
            {
                networkManager.ClientManager.Broadcast(new JoinTicketBroadcast { Ticket = pendingJoinTicket });
            }
            else if (args.ConnectionState == LocalConnectionState.Stopped)
            {
                authenticatedCompletion?.TrySetException(new InvalidOperationException("FishNet client disconnected before authentication."));
            }
        }

        private void OnAuthenticated()
        {
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