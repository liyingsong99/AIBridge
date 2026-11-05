using System;
using System.Collections.Generic;
using AIBridge.Internal.Json;

namespace AIBridge.Editor
{
    public static partial class PipelineCodeProtocol
    {
        private const string RunJobCanceledCode = "pipelineJobCanceled";
        private const string RunJobCanceledText =
            "Pipeline job was canceled; side effects are unknown and are not rolled back. Do not replay automatically.";

        /// <summary>只解释已观察到的 run job 终态；job 结束不等于入口业务成功或副作用已回滚。</summary>
        public static PipelineCodeResult ParseRunJobResult(PipelineJobObservation observation, bool dryRun)
        {
            var httpStatus = observation == null ? 0 : observation.httpStatus;
            try
            {
                if (!ValidRunJobObservation(observation)) return InvalidResponse(httpStatus);
                if (observation.state == "canceled")
                    return new PipelineCodeResult
                    {
                        httpStatus = httpStatus, status = "canceled", mayStillBeRunning = false,
                        errorCode = RunJobCanceledCode, error = RunJobCanceledText
                    };
                if (!(observation.resultValue is Dictionary<string, object> inner)
                    || observation.startedAt == null || !ValidRunPayload(inner)) return InvalidResponse(httpStatus);
                // completed 可以携带 RunScriptResponse.Fail；failed 则不能携带成功 payload 或冲突的错误元数据。
                if (observation.state == "failed" && !ConsistentRunJobFailure(observation, inner))
                    return InvalidResponse(httpStatus);
                // 直接复用同步 parser 的原始类型校验与业务分支，不补造字段，也不经序列化归一化数值类型。
                var result = new PipelineCodeResult { httpStatus = httpStatus };
                if (!ReadDiagnostics(inner, result)) return InvalidResponse(httpStatus);
                result = IsTrue(inner, "success") ? ReadRunSuccess(inner, result, dryRun) : ReadRunFailure(inner, result, dryRun);
                // 裸 job DTO 没有 duration；不能由时间戳或阶段耗时推造执行耗时。
                result.executionTimeMs = null;
                if (!result.success && !result.mayStillBeRunning) result.status = "failed";
                return result;
            }
            catch (Exception)
            {
                return InvalidResponse(httpStatus);
            }
        }

        private static bool ValidRunJobObservation(PipelineJobObservation observation)
        {
            if (observation == null || !observation.valid || observation.status != "observed"
                || observation.httpStatus != 200 || observation.command != RunCommand || !observation.executionEnded
                || !observation.sideEffectsUnknown) return false;
            if (observation.state != "completed" && observation.state != "failed" && observation.state != "canceled")
                return false;
            // DTO 可被调用者修改；复用 job parser 复验身份、UTC 时序、终态字段及取消/进度矛盾。
            var data = new Dictionary<string, object>
            {
                { "jobId", observation.jobId }, { "command", observation.command }, { "state", observation.state },
                { "cancellationRequested", observation.cancellationRequested }, { "enqueuedAt", observation.enqueuedAt },
                { "startedAt", observation.startedAt }, { "completedAt", observation.completedAt },
                { "result", observation.resultValue }, { "error", observation.error },
                { "errorDetails", observation.errorDetails }, { "errorCode", observation.errorCode },
                { "progress", observation.progress }, { "warnings", observation.warnings }
            };
            var verified = PipelineJobProtocol.ParseStatus(AIBridgeJson.Serialize(AIBridgeJson.PreserveObjectNulls(data)),
                observation.httpStatus, observation.jobId, RunCommand);
            return verified.valid && verified.executionEnded && verified.notStarted == observation.notStarted;
        }

        private static bool ConsistentRunJobFailure(PipelineJobObservation observation, Dictionary<string, object> inner)
        {
            return Value(inner, "success") is bool success && !success
                && observation.error == Text(inner, "error")
                && observation.errorDetails == Text(inner, "errorDetails")
                && observation.errorCode == Text(inner, "errorCode");
        }
    }
}
