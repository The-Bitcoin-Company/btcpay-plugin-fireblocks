using System.Globalization;
using System.Net;
using System.Text.Json;
using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Fireblocks.Core;

public sealed class SweepRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string VaultId { get; set; } = "";
    public string AssetId { get; set; } = "";
    public string Environment { get; set; } = "";
    public string WorkspaceFingerprint { get; set; } = "";
    public string DestinationType { get; set; } = "Wallet";
    public string Destination { get; set; } = "";
    public string Amount { get; set; } = "";
    public string State { get; set; } = "Confirmation";
    public bool Automatic { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public string? TransactionId { get; set; }
    public string ProviderStatus { get; set; } = "";
    public string Diagnostic { get; set; } = "";
    public bool Unresolved => State == "Pending";
}

public sealed record SweepExchangeAccount(string Id, string Name);

public sealed class VaultSweepSettings
{
    public string DestinationType { get; set; } = "Wallet";
    public string Destination { get; set; } = "";
    public List<SweepRecord> Records { get; set; } = [];
    public bool AutomationEnabled { get; set; }
    public int CheckIntervalMinutes { get; set; } = 60;
    public string MinimumAvailableBalance { get; set; } = "";
    public DateTimeOffset? LastCheckAt { get; set; }
    public string CheckDiagnostic { get; set; } = "";
}

public interface IVaultSweepStore
{
    Task<VaultSweepSettings> Get(string storeId);
    Task Save(string storeId, VaultSweepSettings settings);
    Task<string[]> StoreIds();
}

// Independent of checkout configuration; use the collection-vault database lock for
// every mutation. Saves commit before side effects, independently of the lock lease.
public sealed class VaultSweepStore(StoreRepository stores) : IVaultSweepStore
{
    public async Task<VaultSweepSettings> Get(string storeId) =>
        await stores.GetSettingAsync<VaultSweepSettings>(storeId, "Fireblocks.VaultSweeps") ?? new();
    public Task Save(string storeId, VaultSweepSettings settings) => stores.UpdateSetting(storeId, "Fireblocks.VaultSweeps", settings);
    public async Task<string[]> StoreIds() => (await stores.GetStores()).Select(s => s.Id).ToArray();
}

public sealed class VaultSweepService(CoreApiClient client, ICoreApiSettingsStore credentials,
    IVaultProvisioningStore vaults, IVaultSweepStore store, ILogger<VaultSweepService>? logger = null,
    TimeProvider? timeProvider = null)
{
    public Task<VaultSweepSettings> Get(string storeId) => store.Get(storeId);

    private async Task<(VaultBinding Vault, CoreApiSettings Credentials)> Context(string storeId)
    {
        var settings = await credentials.Get(storeId);
        var vault = await vaults.Get(storeId);
        if (vault is not { State: "Success", VaultAccountId: not null } ||
            vault.WorkspaceFingerprint != VaultProvisioningService.Fingerprint(settings) ||
            vault.AssetId is not ("USDC_SOL" or "USDC_SOL_TEST" or "SOL_TEST"))
            throw new CoreApiException("Restore the ready collection vault's original workspace before sweeping.");
        return (vault, settings);
    }

    public async Task<IReadOnlyList<SweepExchangeAccount>> ExchangeAccounts(string storeId)
    {
        var (_, settings) = await Context(storeId);
        var accounts = new List<SweepExchangeAccount>();
        var cursors = new HashSet<string>();
        string? after = null;
        do
        {
            var page = await client.Send(settings, HttpMethod.Get, "/exchange_accounts/paged?limit=100" +
                (after is null ? "" : "&after=" + Uri.EscapeDataString(after)));
            if (page.ValueKind != JsonValueKind.Object || !page.TryGetProperty("exchanges", out var exchanges) || exchanges.ValueKind != JsonValueKind.Array)
                throw new CoreApiException("Connected exchange accounts could not be verified.");
            foreach (var account in exchanges.EnumerateArray())
                if (Text(account, "status") == "APPROVED" && Text(account, "id") is { Length: > 0 } id)
                    accounts.Add(new(id, Text(account, "name") ?? id));
            after = page.TryGetProperty("paging", out var paging) ? Text(paging, "after") : null;
            if (!string.IsNullOrEmpty(after) && (!cursors.Add(after) || cursors.Count > 100))
                throw new CoreApiException("Connected exchange account discovery is incomplete. Retry later.");
        } while (!string.IsNullOrEmpty(after));
        return accounts;
    }

    private async Task ValidateDestination(CoreApiSettings settings, VaultBinding vault, string type, string destination)
    {
        if (type == "Wallet")
        {
            if (!FireblocksStoreConfig.IsValidSolanaAddress(destination) || destination == vault.Address)
                throw new CoreApiException("Enter a valid same-network wallet address different from the collection vault.");
            return;
        }
        if (type != "Exchange" || string.IsNullOrWhiteSpace(destination))
            throw new CoreApiException("Select a wallet or connected exchange destination first.");
        var path = "/exchange_accounts/" + Uri.EscapeDataString(destination);
        var account = await client.Send(settings, HttpMethod.Get, path);
        if (Text(account, "id") != destination || Text(account, "status") != "APPROVED")
            throw new CoreApiException("The exchange account is unavailable or not approved in this workspace.");
        var asset = await client.Send(settings, HttpMethod.Get, path + "/" + Uri.EscapeDataString(vault.AssetId));
        // Never infer network support from a ticker or an exchange balance. The
        // Fireblocks network-qualified identity must be explicitly established.
        var legacyId = Text(asset, "assetLegacyId") ?? Text(asset, "id");
        if (legacyId != vault.AssetId)
            throw new CoreApiException("The exchange destination's exact asset/network could not be verified. No transfer was submitted.");
    }

    public async Task SaveDestination(string storeId, string? destination, string destinationType = "Wallet")
    {
        destination = destination?.Trim() ?? "";
        await using var lease = await vaults.Lock(storeId);
        var (vault, settings) = await Context(storeId);
        await ValidateDestination(settings, vault, destinationType, destination);
        var saved = await store.Get(storeId);
        if (saved.Destination != destination || saved.DestinationType != destinationType)
        {
            saved.AutomationEnabled = false;
            foreach (var old in saved.Records.Where(r => r.State == "Confirmation")) old.State = "Expired";
        }
        saved.DestinationType = destinationType;
        saved.Destination = destination;
        await store.Save(storeId, saved);
    }

    private async Task<decimal> Balance(CoreApiSettings settings, VaultBinding vault)
    {
        var wallet = await client.Send(settings, HttpMethod.Get, "/vault/accounts/" + Uri.EscapeDataString(vault.VaultAccountId!) + "/" + Uri.EscapeDataString(vault.AssetId));
        if (!decimal.TryParse(Text(wallet, "available"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var balance) || balance <= 0)
            throw new CoreApiException("Available balance is unavailable or zero. No transfer was submitted.");
        return balance;
    }

    public static decimal ValidateAmount(string? amount, string assetId, decimal available)
    {
        var precision = assetId == "SOL_TEST" ? 9 : 6;
        if (string.IsNullOrEmpty(amount) || amount.Length > 40 ||
            amount.Any(c => c is not (>= '0' and <= '9') && c != '.') ||
            (amount.Contains('.') && amount.Length - amount.IndexOf('.') - 1 > precision) ||
            !decimal.TryParse(amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
            value <= 0 || value > available || decimal.Round(value, precision) != value)
            throw new CoreApiException("Enter a positive amount no greater than the available balance, within the asset's decimal precision.");
        return value;
    }

    public async Task<SweepRecord> Prepare(string storeId)
    {
        await using var lease = await vaults.Lock(storeId);
        var (vault, settings) = await Context(storeId);
        var saved = await store.Get(storeId);
        if (saved.Records.Any(r => r.Unresolved)) throw new CoreApiException("An earlier sweep is unresolved. Refresh its status; do not submit another transfer.");
        await ValidateDestination(settings, vault, saved.DestinationType, saved.Destination);
        var balance = await Balance(settings, vault);
        var record = new SweepRecord
        {
            VaultId = vault.VaultAccountId!, AssetId = vault.AssetId, Environment = vault.Environment,
            WorkspaceFingerprint = vault.WorkspaceFingerprint, DestinationType = saved.DestinationType, Destination = saved.Destination,
            Amount = balance.ToString(CultureInfo.InvariantCulture)
        };
        // Only the newest confirmation can be used. Old tabs cannot trigger a later sweep.
        foreach (var old in saved.Records.Where(r => r.State == "Confirmation")) old.State = "Expired";
        saved.Records.Add(record);
        await store.Save(storeId, saved);
        return record;
    }

    public async Task<SweepRecord> Confirm(string storeId, string id, string? amount)
    {
        await using var lease = await vaults.Lock(storeId);
        var saved = await store.Get(storeId);
        var record = saved.Records.SingleOrDefault(r => r.Id == id) ?? throw new CoreApiException("Sweep confirmation not found. Start again from settings.");
        // Repeated forms, including after completion, never allocate another identity.
        if (record.State != "Confirmation") return record;
        if (saved.Records.Any(r => r.Unresolved)) throw new CoreApiException("An earlier sweep is unresolved. Refresh its status first.");
        var (vault, settings) = await Context(storeId);
        if (record.WorkspaceFingerprint != vault.WorkspaceFingerprint || record.VaultId != vault.VaultAccountId ||
            record.AssetId != vault.AssetId || record.Destination != saved.Destination || record.DestinationType != saved.DestinationType ||
            DateTimeOffset.UtcNow - record.CreatedAt > TimeSpan.FromMinutes(15))
            throw new CoreApiException("Sweep settings changed or confirmation expired. Start again from settings.");
        await ValidateDestination(settings, vault, record.DestinationType, record.Destination);
        var available = await Balance(settings, vault);
        record.Amount = ValidateAmount(amount, record.AssetId, available).ToString(CultureInfo.InvariantCulture);
        return await Submit(storeId, saved, record, settings);
    }

    // Called only while holding the shared vault lock, by manual and automatic paths.
    private async Task<SweepRecord> Submit(string storeId, VaultSweepSettings saved, SweepRecord record, CoreApiSettings settings)
    {
        record.State = "Pending";
        record.SubmittedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        record.Diagnostic = "Submission is unresolved. Refresh to reconcile; no automatic resubmission is performed.";
        // Never POST this record again, even if the process dies before the request.
        // The permanent externalTxId, not the expiring HTTP key, is the recovery identity.
        await store.Save(storeId, saved);
        try
        {
            var result = await client.Send(settings, HttpMethod.Post, "/transactions", new
            {
                operation = "TRANSFER", externalTxId = record.Id, assetId = record.AssetId,
                source = new { type = "VAULT_ACCOUNT", id = record.VaultId },
                destination = record.DestinationType == "Exchange"
                    ? (object)new { type = "EXCHANGE_ACCOUNT", id = record.Destination }
                    : new { type = "ONE_TIME_ADDRESS", oneTimeAddress = new { address = record.Destination } },
                amount = record.Amount
            }, record.Id);
            var transactionId = Text(result, "id");
            record.TransactionId = string.IsNullOrWhiteSpace(transactionId) ? null : transactionId;
            // Submission is not completion, even if a create response claims completion.
            record.Diagnostic = "Submitted; awaiting provider transaction verification. Approvals must be handled in Fireblocks.";
        }
        catch (CoreApiException e)
        {
            if (e.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity)
                record.State = "Failed";
            record.Diagnostic = record.State == "Failed"
                ? "Submission rejected. Check native SOL gas, available balance, policy and permissions in Fireblocks. No automatic remediation is performed."
                : "Submission is uncertain. Refresh to reconcile by external ID. Do not create another transfer in the console.";
        }
        await store.Save(storeId, saved);
        return record;
    }

    public async Task SaveAutomation(string storeId, bool enabled, int intervalMinutes, string? minimum)
    {
        await using var lease = await vaults.Lock(storeId);
        var saved = await store.Get(storeId);
        // Disabling must always remain possible, even with missing credentials or bad inputs.
        if (enabled)
        {
            var (vault, _) = await Context(storeId);
            ValidateAutomation(intervalMinutes, minimum, vault.AssetId);
            if (!FireblocksStoreConfig.IsValidSolanaAddress(saved.Destination) || saved.Destination == vault.Address)
                throw new CoreApiException("Save a different same-network sweep destination before enabling automation.");
            saved.CheckIntervalMinutes = intervalMinutes;
            saved.MinimumAvailableBalance = minimum!;
        }
        saved.AutomationEnabled = enabled;
        await store.Save(storeId, saved);
    }

    private static decimal ValidateAutomation(int interval, string? minimum, string assetId)
    {
        if (interval is < 1 or > 525600)
            throw new CoreApiException("Check interval must be between 1 and 525600 minutes.");
        return ValidateAmount(minimum, assetId, decimal.MaxValue);
    }

    public async Task CheckAutomation(string storeId)
    {
        await using var lease = await vaults.Lock(storeId);
        var saved = await store.Get(storeId);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (!saved.AutomationEnabled) return;
        // Validate persisted intervals before arithmetic: corrupted configuration fails closed.
        if (saved.CheckIntervalMinutes is >= 1 and <= 525600 && saved.LastCheckAt is { } last &&
            now - last < TimeSpan.FromMinutes(saved.CheckIntervalMinutes)) return;
        saved.LastCheckAt = now;
        saved.CheckDiagnostic = "Check interrupted or unresolved. No automatic retry of a submission is performed.";
        await store.Save(storeId, saved);
        try
        {
            var (vault, settings) = await Context(storeId);
            var minimum = ValidateAutomation(saved.CheckIntervalMinutes, saved.MinimumAvailableBalance, vault.AssetId);
            if (!FireblocksStoreConfig.IsValidSolanaAddress(saved.Destination) || saved.Destination == vault.Address)
                throw new CoreApiException("Invalid saved destination.");
            if (saved.Records.Any(r => r.Unresolved))
                saved.CheckDiagnostic = "Skipped: an earlier sweep is unresolved.";
            else
            {
                var available = await Balance(settings, vault);
                if (available < minimum) saved.CheckDiagnostic = "Skipped: available balance is below the minimum.";
                else
                {
                    var record = new SweepRecord
                    {
                        CreatedAt = now, Automatic = true, VaultId = vault.VaultAccountId!, AssetId = vault.AssetId,
                        Environment = vault.Environment, WorkspaceFingerprint = vault.WorkspaceFingerprint,
                        Destination = saved.Destination,
                        Amount = ValidateAmount(available.ToString(CultureInfo.InvariantCulture), vault.AssetId, available).ToString(CultureInfo.InvariantCulture)
                    };
                    foreach (var old in saved.Records.Where(r => r.State == "Confirmation")) old.State = "Expired";
                    saved.Records.Add(record);
                    saved.CheckDiagnostic = "Eligible: see the shared sweep history for submission and provider status.";
                    await Submit(storeId, saved, record, settings);
                    return;
                }
            }
        }
        catch (CoreApiException)
        {
            saved.CheckDiagnostic = "Check failed. Verify original workspace, settings, available balance and connectivity in Fireblocks. No automatic remediation is performed.";
        }
        await store.Save(storeId, saved);
    }

    public async Task Refresh(string storeId)
    {
        await using var lease = await vaults.Lock(storeId);
        var saved = await store.Get(storeId);
        foreach (var record in saved.Records.Where(r => r.Unresolved))
        {
            try
            {
                var settings = await credentials.Get(storeId);
                if (record.WorkspaceFingerprint != VaultProvisioningService.Fingerprint(settings))
                    throw new CoreApiException("Restore the original workspace credentials to reconcile this sweep.");
                var path = string.IsNullOrWhiteSpace(record.TransactionId)
                    ? "/transactions/external_tx_id/" + Uri.EscapeDataString(record.Id)
                    : "/transactions/" + Uri.EscapeDataString(record.TransactionId);
                var result = await client.Send(settings, HttpMethod.Get, path);
                var providerId = Text(result, "id");
                if (string.IsNullOrWhiteSpace(providerId) || Text(result, "externalTxId") != record.Id ||
                    Text(result, "assetId") != record.AssetId)
                    throw new CoreApiException("Provider transaction identity could not be verified. Reconcile in Fireblocks; this sweep remains blocked.");
                record.TransactionId = providerId;
                var status = Text(result, "status");
                // Never display free-form provider diagnostics or unknown status strings.
                record.ProviderStatus = status is "COMPLETED" or "FAILED" or "REJECTED" or "CANCELLED" or "BLOCKED" or "TIMEOUT" or
                    "SUBMITTED" or "QUEUED" or "CANCELLING" or "PENDING_AUTHORIZATION" or "PENDING_SIGNATURE" or "PENDING_3RD_PARTY_MANUAL_APPROVAL" or
                    "PENDING_3RD_PARTY" or "BROADCASTING" or "CONFIRMING" or "PENDING_AML_SCREENING" or "PARTIALLY_COMPLETED"
                    ? status : "UNKNOWN";
                record.State = status == "COMPLETED" ? "Completed" : status is "FAILED" or "REJECTED" or "CANCELLED" or "BLOCKED" or "TIMEOUT" ? "Failed" : "Pending";
                record.Diagnostic = record.State switch
                {
                    "Completed" => "Provider confirmed completion.",
                    "Failed" => "Provider reports failure. Check native SOL gas, available balance, policy and rejection details in Fireblocks. No automatic remediation is performed.",
                    _ => "Awaiting provider completion. Handle policy, approvals or signing in Fireblocks. No automatic approval, fueling or resubmission is performed."
                };
            }
            catch (CoreApiException)
            {
                record.Diagnostic = "Status could not be verified. Restore the original workspace and check connectivity or reconcile external ID in Fireblocks. This sweep remains unresolved; no resubmission is performed.";
            }
        }
        await store.Save(storeId, saved);
    }

    public async Task ReconcileOutstanding(CancellationToken cancellationToken)
    {
        foreach (var id in await store.StoreIds())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if ((await store.Get(id)).Records.Any(r => r.Unresolved)) await Refresh(id);
                await CheckAutomation(id);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A corrupt record or database failure in one store must not starve
                // the remaining stores. Never log exception text or provider bodies.
                logger?.LogWarning("A vault sweep store could not be reconciled; continuing with other stores and retrying next pass");
            }
        }
    }

    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
