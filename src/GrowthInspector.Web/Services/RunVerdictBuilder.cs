using GrowthInspector.Core.Models;

namespace GrowthInspector.Web.Services;

/// <summary>
/// 一轮执行的判定分布：每个检查项先经 <see cref="StatusPresenter.FromResult"/> 归一，
/// 再按五态计数。页面只消费这里的数字，不自己写状态 switch。
/// </summary>
public sealed record RunVerdict(int Total, int Ok, int Warning, int Danger, int Pending, int Neutral)
{
    public static RunVerdict Empty { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>需要处理的项：注意 + 异常。</summary>
    public int Attention => Warning + Danger;

    /// <summary>形成判定的项：Ok + 注意 + 异常。</summary>
    public int Judged => Ok + Warning + Danger;

    /// <summary>meter 的可读文案（色块的无障碍替代）。</summary>
    public string MeterLabel
    {
        get
        {
            if (Total == 0)
            {
                return "本轮没有检查项结果";
            }

            var text = $"共 {Total} 个检查项：{Ok} Ok、{Warning} 注意、{Danger} 异常、{Pending} 待证";
            return Neutral > 0 ? $"{text}、{Neutral} 中性" : text;
        }
    }

    /// <summary>某个态在总数中的百分比宽度，供 meter 内联样式使用。</summary>
    public string WidthOf(int count)
        => Total <= 0 ? "width: 0" : $"width: {count * 100d / Total:0.##}%";
}

/// <summary>执行详情 / 日报等处共用的判定聚合。</summary>
public static class RunVerdictBuilder
{
    public static RunVerdict Build(
        IEnumerable<(CheckResultStatus Status, Severity? Severity, StatusItemKind ItemKind)> results)
    {
        var ok = 0;
        var warning = 0;
        var danger = 0;
        var pending = 0;
        var neutral = 0;
        var total = 0;

        foreach (var (status, severity, itemKind) in results)
        {
            total++;
            switch (StatusPresenter.FromResult(status, severity, itemKind).State)
            {
                case StatusState.Ok:
                    ok++;
                    break;
                case StatusState.Warning:
                    warning++;
                    break;
                case StatusState.Danger:
                    danger++;
                    break;
                case StatusState.Pending:
                    pending++;
                    break;
                default:
                    neutral++;
                    break;
            }
        }

        return new RunVerdict(total, ok, warning, danger, pending, neutral);
    }
}
