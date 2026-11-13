namespace EntraUserSync.Core.Configuration;

public static class CloudEndpoints
{
    public static (string AuthorityHost, string GraphRoot, string Scope) Resolve(AzureCloud cloud) => cloud switch
    {
        AzureCloud.Global => (
            "https://login.microsoftonline.com",
            "https://graph.microsoft.com",
            "https://graph.microsoft.com/.default"),
        AzureCloud.USGov => (
            "https://login.microsoftonline.us",
            "https://graph.microsoft.us",
            "https://graph.microsoft.us/.default"),
        AzureCloud.USGovDoD => (
            "https://login.microsoftonline.us",
            "https://dod-graph.microsoft.us",
            "https://dod-graph.microsoft.us/.default"),
        AzureCloud.China => (
            "https://login.chinacloudapi.cn",
            "https://microsoftgraph.chinacloudapi.cn",
            "https://microsoftgraph.chinacloudapi.cn/.default"),
        _ => throw new ArgumentOutOfRangeException(nameof(cloud), cloud, "Unsupported cloud.")
    };

    public static string GraphVersionPath(GraphApiVersion version) => version switch
    {
        GraphApiVersion.V1_0 => "v1.0",
        GraphApiVersion.Beta => "beta",
        _ => throw new ArgumentOutOfRangeException(nameof(version), version, null)
    };
}
