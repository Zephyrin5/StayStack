; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
SS0001 | Reliability | Error | EntityMintingAnalyzer, an entity must not mint an identity (docs/adr/0025)
SS0002 | Reliability | Error | RetryMintingAnalyzer, retried work must not mint an identity (docs/adr/0025)
