using System.Collections.Immutable;

namespace NetScope.Core.Models;

public enum AppTheme { System, Light, Dark }

public sealed record AppSettings
{
    public AppTheme Theme { get; init; } = AppTheme.System;
    public int ForegroundRefreshMilliseconds { get; init; } = 1000;
    public int TrayRefreshMilliseconds { get; init; } = 2000;
    public bool CloseToTray { get; init; } = true;
    public bool StartWithWindows { get; init; }
    public bool TelemetryEnabled { get; init; }
    public ImmutableArray<DiagnosticTarget> DiagnosticTargets { get; init; } =
    [
        new("Microsoft", "www.microsoft.com"),
        new("Cloudflare", "www.cloudflare.com"),
        new("百度", "www.baidu.com")
    ];

    public AppSettings Normalize() => this with
    {
        ForegroundRefreshMilliseconds = Math.Clamp(ForegroundRefreshMilliseconds, 500, 10_000),
        TrayRefreshMilliseconds = Math.Clamp(TrayRefreshMilliseconds, 1000, 30_000),
        TelemetryEnabled = false,
        DiagnosticTargets = DiagnosticTargets.IsDefaultOrEmpty
            ? [new("Microsoft", "www.microsoft.com"), new("Cloudflare", "www.cloudflare.com"), new("百度", "www.baidu.com")]
            : DiagnosticTargets
    };
}
