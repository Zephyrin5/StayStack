using BuildingBlocks.Pagination;
using System.Text.Json.Serialization;
using Transactions.Features.GetTransactions;
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
[JsonSerializable(typeof(GetTransactionsRequest))]
[JsonSerializable(typeof(PagedResponse<TransactionSummary>))]
public partial class TransactionsJsonSerializerContext : JsonSerializerContext;
