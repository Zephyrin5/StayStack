using System.Text.Json.Serialization;
using Transactions.Features.InitiateTransaction;
using Transactions.Features.MarkTransactionFailed;
using Transactions.Features.MarkTransactionSucceeded;
namespace Transactions.Serialization;

[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InitiateTransactionRequest))]
[JsonSerializable(typeof(InitiateTransactionResponse))]
[JsonSerializable(typeof(MarkTransactionSucceededRequest))]
[JsonSerializable(typeof(MarkTransactionSucceededResponse))]
[JsonSerializable(typeof(MarkTransactionFailedRequest))]
[JsonSerializable(typeof(MarkTransactionFailedResponse))]
public partial class TransactionsJsonSerializerContext : JsonSerializerContext;
