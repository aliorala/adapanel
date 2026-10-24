namespace AdaPanel.Core.Dtos.Ssl;

public sealed record SslRepairTarget(string SiteName, string HostName, int Port);
public sealed record SslRepairApplyRequest(string PlanToken);
public sealed record SslRepairCertificate(string Thumbprint, string Subject, DateTime NotAfterUtc, bool Valid,
    IReadOnlyList<string>? ValidationIssues = null);
public sealed record SslRepairOption(string HostName, string BindingInformation,
    SslRepairCertificate? Current, SslRepairCertificate Candidate, string PlanToken, string BlockedReason);
public sealed record SslRepairPlan(string SiteName, string RecordHostName, IReadOnlyList<SslRepairOption> Options);
public sealed record SslRepairResult(bool BindingApplied, bool TlsVerified, string Detail,
    string SiteName, string BindingInformation, string PreviousThumbprint, string AppliedThumbprint, string StoreName);
