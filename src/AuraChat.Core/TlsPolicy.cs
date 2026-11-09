namespace AuraChat.Core;

/// <summary>
/// 出站 TLS 策略：默认严格校验服务器证书（证书链、主机名都按系统规则验证）。
/// 自签证书 / 内网隧道链不全的场景，用环境变量 AURACHAT_ALLOW_INSECURE_TLS=1（或 true）显式放开——
/// 降级必须看得见、可关掉，不再默默接受任何证书。
/// </summary>
public static class TlsPolicy
{
    public const string AllowInsecureEnv = "AURACHAT_ALLOW_INSECURE_TLS";

    /// <summary>是否放行校验失败的证书。每次读取环境变量，测试和运维可以运行中改。</summary>
    public static bool AllowInsecure
    {
        get
        {
            var v = Environment.GetEnvironmentVariable(AllowInsecureEnv);
            return v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
        }
    }
}
