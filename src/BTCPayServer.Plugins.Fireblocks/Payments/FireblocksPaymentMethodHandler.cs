using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Flow;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Fireblocks.Payments;

public sealed class FireblocksPaymentPromptDetails
{
    public required string AttemptId { get; init; }
    public required string FlowId { get; init; }
    public required string PaymentUrl { get; init; }
    public required decimal RequestedUsd { get; init; }
    public required long ExpiresAt { get; init; }
    public long? ProviderExpiresAt { get; init; }
}

public sealed class FireblocksPaymentDetails;

public sealed class FireblocksPaymentMethodHandler(
    IFlowPaymentService paymentService,
    IConfiguration? configuration = null,
    TimeProvider? timeProvider = null) : IPaymentMethodHandler
{
    private readonly bool _allowLocalEndpoints = FlowEndpointSecurity.AllowLocalEndpoints(configuration);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public static readonly PaymentMethodId PaymentMethod = new("FIREBLOCKS-FLOW");

    public PaymentMethodId PaymentMethodId => PaymentMethod;

    public JsonSerializer Serializer { get; } = new()
    {
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore
    };

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = "USD";
        context.Prompt.PaymentMethodFee = 0m;
        context.Prompt.Divisibility = 2;
        context.Prompt.RateDivisibility = 2;
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        if (!string.Equals(context.InvoiceEntity.Currency, "USD", StringComparison.OrdinalIgnoreCase))
            throw new PaymentMethodUnavailableException("Fireblocks Flow currently supports only USD invoices.");
        if (context.InvoiceEntity.Price <= 0)
            throw new PaymentMethodUnavailableException("Fireblocks Flow requires a fixed, positive invoice amount.");

        var config = ParsePaymentMethodConfig(context.PaymentMethodConfig);
        var errors = config.Validate(_allowLocalEndpoints);
        if (errors.Count != 0)
            throw new PaymentMethodUnavailableException("Fireblocks Flow store configuration is incomplete or invalid.");

        try
        {
            var policyExpiresAt = _timeProvider.GetUtcNow().AddMinutes(config.PaymentExpiryMinutes);
            if (context.InvoiceEntity.ExpirationTime > policyExpiresAt)
                context.InvoiceEntity.ExpirationTime = policyExpiresAt;

            var attempt = await paymentService.CreateOrGetPaymentLinkAsync(
                context.Store.Id,
                context.InvoiceEntity.Id,
                context.Prompt.Calculate().Due,
                context.InvoiceEntity.ExpirationTime,
                config);
            if (attempt.FlowId is null || attempt.PaymentUrl is null)
                throw new PaymentMethodUnavailableException("The persisted Flow attempt has no hosted payment link.");

            context.Prompt.Destination = attempt.PaymentUrl;
            context.Prompt.Details = JObject.FromObject(new FireblocksPaymentPromptDetails
            {
                AttemptId = attempt.Id,
                FlowId = attempt.FlowId,
                PaymentUrl = attempt.PaymentUrl,
                RequestedUsd = attempt.RequestedUsd,
                ExpiresAt = attempt.ExpiresAt.ToUnixTimeSeconds(),
                ProviderExpiresAt = attempt.ProviderExpiresAt?.ToUnixTimeSeconds()
            }, Serializer);
        }
        catch (FlowPaymentCreationException exception)
        {
            // The service message is already redacted. Do not attach the original
            // provider exception, which BTCPay would include in invoice logs.
            throw new PaymentMethodUnavailableException(exception.Message);
        }
    }

    public FireblocksStoreConfig ParsePaymentMethodConfig(JToken config) =>
        FireblocksStoreConfig.Parse(config, Serializer);

    object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken config) => ParsePaymentMethodConfig(config);

    public FireblocksPaymentPromptDetails ParsePaymentPromptDetails(JToken details) =>
        details.ToObject<FireblocksPaymentPromptDetails>(Serializer)
        ?? throw new FormatException("Invalid Fireblocks payment prompt details");

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details) => ParsePaymentPromptDetails(details);

    public FireblocksPaymentDetails ParsePaymentDetails(JToken details) =>
        details.ToObject<FireblocksPaymentDetails>(Serializer) ?? new FireblocksPaymentDetails();

    object IPaymentMethodHandler.ParsePaymentDetails(JToken details) => ParsePaymentDetails(details);

    public Task ValidatePaymentMethodConfig(PaymentMethodConfigValidationContext validationContext)
    {
        var config = ParsePaymentMethodConfig(validationContext.Config);
        var previous = validationContext.PreviousConfig is null ? null : ParsePaymentMethodConfig(validationContext.PreviousConfig);
        config.PreserveCredential(previous);
        foreach (var error in config.Validate(_allowLocalEndpoints))
            validationContext.ModelState.AddModelError(error.Field, error.Message);

        // Preserve the exact validated token, including the write-only credential.
        // ParsePaymentMethodConfig intentionally redacts it again on GET responses.
        validationContext.Config = config.ToStorageToken(Serializer);
        validationContext.StripUnknownProperties = false;
        return Task.CompletedTask;
    }
}
