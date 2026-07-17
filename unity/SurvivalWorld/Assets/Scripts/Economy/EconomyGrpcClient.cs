using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Survival.V1;

namespace SurvivalWorld.Economy
{
    public interface IEconomyClient
    {
        Task<RegisterBuyerResult> RegisterBuyerAsync(RegisterBuyerRequestData request, CancellationToken cancellationToken);
        Task<CommitPurchaseResponse> CommitPurchaseAsync(CommitPurchaseRequest request, CancellationToken cancellationToken);
        Task<CommitSaleResponse> CommitSaleAsync(CommitSaleRequest request, CancellationToken cancellationToken);
        Task<BuyerDespawnResult> DespawnBuyerAsync(string buyerInstanceId, BuyerStatus targetStatus, CancellationToken cancellationToken);
    }

    public sealed class RegisterBuyerRequestData
    {
        public string IdempotencyKey { get; set; }
        public string WorldId { get; set; }
        public string RegionId { get; set; }
        public long Seed { get; set; }
        public string InventoryTableId { get; set; }
        public int PriceModifierBasisPoints { get; set; } = 10000;
        public long SpawnAtUnixMs { get; set; }
        public long DespawnAtUnixMs { get; set; }
    }

    public sealed class RegisterBuyerResult
    {
        private RegisterBuyerResult(bool success, string buyerInstanceId, IReadOnlyList<BuyerStockEntry> stock, string error)
        {
            Success = success;
            BuyerInstanceId = buyerInstanceId ?? string.Empty;
            Stock = stock ?? Array.Empty<BuyerStockEntry>();
            Error = error ?? string.Empty;
        }

        public bool Success { get; }
        public string BuyerInstanceId { get; }
        public IReadOnlyList<BuyerStockEntry> Stock { get; }
        public string Error { get; }

        public static RegisterBuyerResult Ok(string buyerInstanceId, IReadOnlyList<BuyerStockEntry> stock)
        {
            return new RegisterBuyerResult(true, buyerInstanceId, stock, string.Empty);
        }

        public static RegisterBuyerResult Failed(string error)
        {
            return new RegisterBuyerResult(false, string.Empty, Array.Empty<BuyerStockEntry>(), error);
        }
    }

    public readonly struct BuyerDespawnResult
    {
        private BuyerDespawnResult(bool success, string error)
        {
            Success = success;
            Error = error ?? string.Empty;
        }

        public bool Success { get; }
        public string Error { get; }

        public static BuyerDespawnResult Ok()
        {
            return new BuyerDespawnResult(true, string.Empty);
        }

        public static BuyerDespawnResult Failed(string error)
        {
            return new BuyerDespawnResult(false, error);
        }
    }

    public sealed class GeneratedEconomyGrpcClient : IEconomyClient
    {
        private readonly EconomyService.EconomyServiceClient client;

        public GeneratedEconomyGrpcClient(EconomyService.EconomyServiceClient client)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public Task<RegisterBuyerResult> RegisterBuyerAsync(RegisterBuyerRequestData request, CancellationToken cancellationToken)
        {
            return Task.FromResult(RegisterBuyerResult.Failed("RegisterBuyer RPC is not available in the current generated economy proto."));
        }

        public async Task<CommitPurchaseResponse> CommitPurchaseAsync(CommitPurchaseRequest request, CancellationToken cancellationToken)
        {
            return await client.CommitPurchaseAsync(request, cancellationToken: cancellationToken).ResponseAsync;
        }

        public async Task<CommitSaleResponse> CommitSaleAsync(CommitSaleRequest request, CancellationToken cancellationToken)
        {
            return await client.CommitSaleAsync(request, cancellationToken: cancellationToken).ResponseAsync;
        }

        public Task<BuyerDespawnResult> DespawnBuyerAsync(string buyerInstanceId, BuyerStatus targetStatus, CancellationToken cancellationToken)
        {
            return Task.FromResult(BuyerDespawnResult.Failed("DespawnBuyer RPC is not available in the current generated economy proto."));
        }
    }

    public sealed class NullEconomyClient : IEconomyClient
    {
        public static readonly NullEconomyClient Instance = new NullEconomyClient();

        private NullEconomyClient()
        {
        }

        public Task<RegisterBuyerResult> RegisterBuyerAsync(RegisterBuyerRequestData request, CancellationToken cancellationToken)
        {
            return Task.FromResult(RegisterBuyerResult.Failed("Economy client is not configured."));
        }

        public Task<CommitPurchaseResponse> CommitPurchaseAsync(CommitPurchaseRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new CommitPurchaseResponse { Status = PurchaseStatus.Rejected });
        }

        public Task<CommitSaleResponse> CommitSaleAsync(CommitSaleRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new CommitSaleResponse { Status = ResultStatus.Rejected });
        }

        public Task<BuyerDespawnResult> DespawnBuyerAsync(string buyerInstanceId, BuyerStatus targetStatus, CancellationToken cancellationToken)
        {
            return Task.FromResult(BuyerDespawnResult.Failed("Economy client is not configured."));
        }
    }
}
