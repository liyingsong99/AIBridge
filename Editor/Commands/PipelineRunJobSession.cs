using System.Threading.Tasks;

namespace AIBridge.Editor
{
    /// <summary>核心与可选适配器的受限作业会话；调用方不能传入远端 ID、URL 或凭据。</summary>
    public abstract class PipelineRunJobSession
    {
        public abstract string TargetIdentity { get; }
        public abstract string Generation { get; }
        public abstract Task<PipelineJobObservation> SubmitAsync();
        public abstract Task<PipelineJobObservation> ObserveAsync();
        public abstract Task<PipelineJobObservation> CancelAsync(bool confirmed);
        // unknown 只释放本地会话；适配器必须继续保留尚可能被读取的源码快照。
        public abstract void Release(bool executionEnded);
    }
}
