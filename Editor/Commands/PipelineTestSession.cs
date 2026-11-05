using System;
using System.Threading.Tasks;

namespace AIBridge.Editor
{
    /// <summary>受限同步测试选项；不映射 native 正则/精确过滤，也不开放异步或 detached。</summary>
    public sealed class PipelineTestOptions
    {
        public const int MinTimeoutMs = 1000;
        public const int MaxTimeoutMs = 600000;
        public string Filter { get; }
        public string FilterType { get; }
        public int TimeoutMs { get; }
        public string ExpectedFilterApplied => Filter.Length == 0 ? null : FilterType.ToLowerInvariant() + ": " + Filter;

        public PipelineTestOptions(string filter, string filterType, int timeoutMs)
        {
            filter = filter ?? string.Empty;
            if (filter.Length > 4096 || filter.IndexOf('\0') >= 0)
                throw new ArgumentException("Pipeline filter is invalid or too long.", nameof(filter));
            filterType = filterType ?? "testName";
            switch (filterType)
            {
                case "testName": case "assembly": case "category": break;
                default: throw new ArgumentException("Pipeline filter-type must be testName, assembly or category.", nameof(filterType));
            }
            if (timeoutMs < MinTimeoutMs || timeoutMs > MaxTimeoutMs) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            Filter = filter; FilterType = filterType; TimeoutMs = timeoutMs;
        }
    }

    /// <summary>父适配器在主线程 Prepare 后返回此会话；身份不含 token，Submit 必须至多一次。</summary>
    public abstract class PipelineTestSession
    {
        public abstract string TargetIdentity { get; }
        public abstract string Generation { get; }
        public abstract Task<PipelineTestResult> SubmitAsync();
        // 只释放本地会话，不调用上游全局 test cancel；false 不代表测试已结束。
        public abstract void Release(bool executionEnded);
    }
}
