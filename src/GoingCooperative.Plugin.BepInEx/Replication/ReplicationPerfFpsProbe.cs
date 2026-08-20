using System;
using System.Globalization;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const float ReplicationPerfFpsProbeWindowSeconds = 10f;
        private static float replicationPerfWindowStartRealtime;
        private static float replicationPerfWindowElapsed;
        private static float replicationPerfWindowFrameTimeTotal;
        private static float replicationPerfWindowWorstFrameMs;
        private static int replicationPerfWindowFrames;
        private static int replicationPerfWindowOver33Ms;
        private static int replicationPerfWindowOver50Ms;
        private static int replicationPerfWindowOver100Ms;
        private static int replicationPerfLastUpdateFrame = -1;
        // Total wall time spent inside UpdateReplicationRuntime per window. The existing
        // buckets (collect/semantic/pump/...) only cover individually instrumented
        // sections and together accounted for ~4% of frame time, leaving it unknown
        // whether the rest sits in uninstrumented mod code or outside the mod entirely.
        private static double replicationPerfWindowRuntimeUpdateMs;
        private static double replicationPerfWindowRuntimeUpdateMaxMs;

        // Breakdown of the runtime update. runtimeUpdateMs showed ~73% of client frame
        // time inside the mod while the previously instrumented buckets summed to ~4%,
        // so the cost sits in blocks nobody was timing. These split the update into the
        // coarse phases so the next capture names the expensive one instead of guessing.
        private enum ReplicationRuntimeSection
        {
            StorageFlush = 0,
            Pump,
            HostSend,
            ClientDeltaApply,
            ClientPresentation,
            SharedPresentation,
            Count,
        }

        private static readonly double[] ReplicationPerfWindowSectionMs =
            new double[(int)ReplicationRuntimeSection.Count];

        private static readonly string[] ReplicationRuntimeSectionNames =
        {
            "storageFlushMs",
            "pumpSectionMs",
            "hostSendMs",
            "clientDeltaApplyMs",
            "clientPresentationMs",
            "sharedPresentationMs",
        };

        private static long BeginReplicationRuntimeSection()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static void EndReplicationRuntimeSection(ReplicationRuntimeSection section, long startedTimestamp)
        {
            ReplicationPerfWindowSectionMs[(int)section] +=
                (System.Diagnostics.Stopwatch.GetTimestamp() - startedTimestamp)
                * 1000d / System.Diagnostics.Stopwatch.Frequency;
        }

        private static string FormatReplicationRuntimeSectionBreakdown()
        {
            var builder = new System.Text.StringBuilder(160);
            for (var i = 0; i < ReplicationRuntimeSectionNames.Length; i++)
            {
                builder.Append(' ')
                    .Append(ReplicationRuntimeSectionNames[i])
                    .Append('=')
                    .Append(ReplicationPerfWindowSectionMs[i].ToString("0.###", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
        private static bool replicationPerfProbeDisabledLogged;
        private static bool replicationPerfProbeEnabledLogged;
        private static long replicationPerfLastSnapshotsSent;
        private static long replicationPerfLastSnapshotsReceived;
        private static long replicationPerfLastSnapshotsApplied;
        private static long replicationPerfLastWorldDeltasSent;
        private static long replicationPerfLastWorldDeltasReceived;
        private static long replicationPerfLastWorldDeltasApplied;
        private static long replicationPerfLastWorldDeltaRetriesSent;
        private static long replicationPerfLastWorldDeltasCoalesced;
        private static long replicationPerfLastWorldDeltaApplyBudgetStops;
        private static long replicationPerfLastIntentsSent;
        private static long replicationPerfLastIntentsReceived;
        private static long replicationPerfLastCommandAcksSent;
        private static long replicationPerfLastCommandAcksReceived;

        private void UpdateReplicationPerfFpsProbe()
        {
            var frame = Time.frameCount;
            if (replicationPerfLastUpdateFrame == frame)
            {
                return;
            }

            replicationPerfLastUpdateFrame = frame;
            if (!replicationConfigPerfFpsProbe)
            {
                if (!replicationPerfProbeDisabledLogged)
                {
                    replicationPerfProbeDisabledLogged = true;
                    replicationPerfProbeEnabledLogged = false;
                    AppendPluginLog("Going Cooperative perf fps probe disabled");
                }

                ResetReplicationPerfFpsProbe();
                return;
            }

            var now = Time.realtimeSinceStartup;
            if (replicationPerfWindowStartRealtime <= 0f)
            {
                StartReplicationPerfFpsProbeWindow(now);
                if (!replicationPerfProbeEnabledLogged)
                {
                    replicationPerfProbeEnabledLogged = true;
                    replicationPerfProbeDisabledLogged = false;
                    LogReplicationPerfFpsProbeConfigured("first-update");
                }
            }

            var frameSeconds = Math.Max(0f, Time.unscaledDeltaTime);
            var frameMs = frameSeconds * 1000f;
            replicationPerfWindowElapsed = Math.Max(replicationPerfWindowElapsed + frameSeconds, now - replicationPerfWindowStartRealtime);
            replicationPerfWindowFrameTimeTotal += frameSeconds;
            replicationPerfWindowFrames++;
            if (frameMs > replicationPerfWindowWorstFrameMs)
            {
                replicationPerfWindowWorstFrameMs = frameMs;
            }

            if (frameMs >= 33.333f)
            {
                replicationPerfWindowOver33Ms++;
            }

            if (frameMs >= 50f)
            {
                replicationPerfWindowOver50Ms++;
            }

            if (frameMs >= 100f)
            {
                replicationPerfWindowOver100Ms++;
            }

            if (now - replicationPerfWindowStartRealtime >= ReplicationPerfFpsProbeWindowSeconds)
            {
                LogReplicationPerfFpsProbeWindow(now);
                StartReplicationPerfFpsProbeWindow(now);
            }
        }

        private void LogReplicationPerfFpsProbeConfigured(string source)
        {
            AppendPluginLog("Going Cooperative perf fps probe configured source="
                + source
                + " enabled="
                + replicationConfigPerfFpsProbe
                + " windowSeconds="
                + ReplicationPerfFpsProbeWindowSeconds.ToString("0.###", CultureInfo.InvariantCulture)
                + " side="
                + (replicationConfigHostMode ? "host" : "client"));
        }

        private void LogReplicationPerfFpsProbeWindow(float now)
        {
            if (replicationPerfWindowFrames <= 0)
            {
                return;
            }

            var elapsed = Math.Max(0.001f, replicationPerfWindowElapsed);
            var avgFps = replicationPerfWindowFrames / elapsed;
            var avgFrameMs = (replicationPerfWindowFrameTimeTotal / Math.Max(1, replicationPerfWindowFrames)) * 1000f;
            var minFps = replicationPerfWindowWorstFrameMs > 0f ? 1000f / replicationPerfWindowWorstFrameMs : 0f;
            var snapshotsSent = replicationSnapshotsSent - replicationPerfLastSnapshotsSent;
            var snapshotsReceived = replicationSnapshotsReceived - replicationPerfLastSnapshotsReceived;
            var snapshotsApplied = replicationSnapshotsApplied - replicationPerfLastSnapshotsApplied;
            var worldDeltasSent = replicationWorldObjectDeltasSent - replicationPerfLastWorldDeltasSent;
            var worldDeltasReceived = replicationWorldObjectDeltasReceived - replicationPerfLastWorldDeltasReceived;
            var worldDeltasApplied = replicationWorldObjectDeltasApplied - replicationPerfLastWorldDeltasApplied;
            var worldDeltaRetries = replicationWorldObjectDeltaRetriesSent - replicationPerfLastWorldDeltaRetriesSent;
            var worldDeltasCoalesced = replicationWorldObjectDeltasCoalesced - replicationPerfLastWorldDeltasCoalesced;
            var worldDeltaApplyBudgetStops = replicationWorldObjectDeltaApplyBudgetStops - replicationPerfLastWorldDeltaApplyBudgetStops;
            var worldDeltaQueue = GetPendingReplicationWorldObjectDeltaApplyCount();
            var worldDeltaCoalescableQueue = GetCoalescableReplicationWorldObjectDeltaApplyCount();
            var intentsSent = replicationIntentsSent - replicationPerfLastIntentsSent;
            var intentsReceived = replicationIntentsReceived - replicationPerfLastIntentsReceived;
            var commandAcksSent = replicationCommandAcksSent - replicationPerfLastCommandAcksSent;
            var commandAcksReceived = replicationCommandAcksReceived - replicationPerfLastCommandAcksReceived;

            LogReplicationInfo("Going Cooperative perf fps window side="
                + (replicationConfigHostMode ? "host" : "client")
                // Lets a multi-minute stall be cross-referenced against real wall-clock
                // time (autosave schedule, OS/AV activity) even when it isn't caused by
                // a focus/pause change and so has no OnApplicationFocus/Pause log line.
                + " wallClockUtc="
                + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                + " elapsed="
                + elapsed.ToString("0.###", CultureInfo.InvariantCulture)
                + " frames="
                + replicationPerfWindowFrames.ToString(CultureInfo.InvariantCulture)
                + " avgFps="
                + avgFps.ToString("0.0", CultureInfo.InvariantCulture)
                + " minFps="
                + minFps.ToString("0.0", CultureInfo.InvariantCulture)
                + " avgMs="
                + avgFrameMs.ToString("0.0", CultureInfo.InvariantCulture)
                + " worstMs="
                + replicationPerfWindowWorstFrameMs.ToString("0.0", CultureInfo.InvariantCulture)
                + " over33ms="
                + replicationPerfWindowOver33Ms.ToString(CultureInfo.InvariantCulture)
                + " over50ms="
                + replicationPerfWindowOver50Ms.ToString(CultureInfo.InvariantCulture)
                + " over100ms="
                + replicationPerfWindowOver100Ms.ToString(CultureInfo.InvariantCulture)
                + " runtimeUpdateMs="
                + replicationPerfWindowRuntimeUpdateMs.ToString("0.###", CultureInfo.InvariantCulture)
                + " runtimeUpdateMaxMs="
                + replicationPerfWindowRuntimeUpdateMaxMs.ToString("0.###", CultureInfo.InvariantCulture)
                + FormatReplicationRuntimeSectionBreakdown()
                + " snapshotsSent="
                + snapshotsSent.ToString(CultureInfo.InvariantCulture)
                + " snapshotsReceived="
                + snapshotsReceived.ToString(CultureInfo.InvariantCulture)
                + " snapshotsApplied="
                + snapshotsApplied.ToString(CultureInfo.InvariantCulture)
                + " worldDeltasSent="
                + worldDeltasSent.ToString(CultureInfo.InvariantCulture)
                + " worldDeltasReceived="
                + worldDeltasReceived.ToString(CultureInfo.InvariantCulture)
                + " worldDeltasApplied="
                + worldDeltasApplied.ToString(CultureInfo.InvariantCulture)
                + " worldDeltaRetries="
                + worldDeltaRetries.ToString(CultureInfo.InvariantCulture)
                + " buildingSnapshotCollectMs="
                + replicationBuildingStateSnapshotLastCollectionMs.ToString("0.###", CultureInfo.InvariantCulture)
                + " buildingSnapshotQueueMs="
                + replicationBuildingStateSnapshotLastQueueMs.ToString("0.###", CultureInfo.InvariantCulture)
                + " buildingSnapshotDeferred="
                + replicationBuildingStateSnapshotChangeDeferred
                + " buildingSnapshotDeferredChanges="
                + replicationBuildingStateSnapshotDeferredChangeCount.ToString(CultureInfo.InvariantCulture)
                + " "
                + FormatReplicationBuildingLifecycleV2Status()
                + " worldDeltasCoalesced="
                + worldDeltasCoalesced.ToString(CultureInfo.InvariantCulture)
                + " worldDeltaApplyBudgetStops="
                + worldDeltaApplyBudgetStops.ToString(CultureInfo.InvariantCulture)
                + " worldDeltaQueue="
                + worldDeltaQueue.ToString(CultureInfo.InvariantCulture)
                + " worldDeltaCoalescableQueue="
                + worldDeltaCoalescableQueue.ToString(CultureInfo.InvariantCulture)
                + " worldDeltaApplyBudgetPerFrame="
                + replicationConfigWorldObjectDeltaApplyBudgetPerFrame.ToString(CultureInfo.InvariantCulture)
                + " worldDeltaApplyBudgetMsPerFrame="
                + replicationConfigWorldObjectDeltaApplyBudgetMsPerFrame.ToString("0.###", CultureInfo.InvariantCulture)
                + " intentsSent="
                + intentsSent.ToString(CultureInfo.InvariantCulture)
                + " intentsReceived="
                + intentsReceived.ToString(CultureInfo.InvariantCulture)
                + " acksSent="
                + commandAcksSent.ToString(CultureInfo.InvariantCulture)
                + " acksReceived="
                + commandAcksReceived.ToString(CultureInfo.InvariantCulture)
                + " diagnostics animation="
                + replicationConfigAnimationDiagnostics
                + " characterState="
                + replicationConfigCharacterStateDiagnostics
                + " resultLifecycle="
                + replicationConfigResultLifecycleProbes
                + " worldDeltaMode="
                + replicationConfigWorldObjectDeltaMode
                + " applySnapshots="
                + replicationConfigApplySnapshots
                + " maxSnapshotEntities="
                + replicationConfigMaxSnapshotEntities.ToString(CultureInfo.InvariantCulture)
                + " snapshotHz="
                + replicationConfigSnapshotHz.ToString(CultureInfo.InvariantCulture)
                + " "
                + FormatReplicationPresentationSmoothingStatus()
                + " "
                + FormatReplicationNeedsStatus()
                + " lastGameTime="
                + (string.IsNullOrEmpty(replicationLastGameTimeSummary) ? "<none>" : replicationLastGameTimeSummary));

            replicationPerfLastSnapshotsSent = replicationSnapshotsSent;
            replicationPerfLastSnapshotsReceived = replicationSnapshotsReceived;
            replicationPerfLastSnapshotsApplied = replicationSnapshotsApplied;
            replicationPerfLastWorldDeltasSent = replicationWorldObjectDeltasSent;
            replicationPerfLastWorldDeltasReceived = replicationWorldObjectDeltasReceived;
            replicationPerfLastWorldDeltasApplied = replicationWorldObjectDeltasApplied;
            replicationPerfLastWorldDeltaRetriesSent = replicationWorldObjectDeltaRetriesSent;
            replicationPerfLastWorldDeltasCoalesced = replicationWorldObjectDeltasCoalesced;
            replicationPerfLastWorldDeltaApplyBudgetStops = replicationWorldObjectDeltaApplyBudgetStops;
            replicationPerfLastIntentsSent = replicationIntentsSent;
            replicationPerfLastIntentsReceived = replicationIntentsReceived;
            replicationPerfLastCommandAcksSent = replicationCommandAcksSent;
            replicationPerfLastCommandAcksReceived = replicationCommandAcksReceived;
        }

        private static void StartReplicationPerfFpsProbeWindow(float now)
        {
            replicationPerfWindowStartRealtime = now;
            replicationPerfWindowElapsed = 0f;
            replicationPerfWindowFrameTimeTotal = 0f;
            replicationPerfWindowWorstFrameMs = 0f;
            replicationPerfWindowFrames = 0;
            replicationPerfWindowOver33Ms = 0;
            replicationPerfWindowOver50Ms = 0;
            replicationPerfWindowOver100Ms = 0;
            replicationPerfWindowRuntimeUpdateMs = 0d;
            replicationPerfWindowRuntimeUpdateMaxMs = 0d;
            Array.Clear(ReplicationPerfWindowSectionMs, 0, ReplicationPerfWindowSectionMs.Length);
            replicationPerfLastUpdateFrame = -1;
        }

        private static void ResetReplicationPerfFpsProbe()
        {
            replicationPerfWindowStartRealtime = 0f;
            replicationPerfWindowElapsed = 0f;
            replicationPerfWindowFrameTimeTotal = 0f;
            replicationPerfWindowWorstFrameMs = 0f;
            replicationPerfWindowFrames = 0;
            replicationPerfWindowOver33Ms = 0;
            replicationPerfWindowOver50Ms = 0;
            replicationPerfWindowOver100Ms = 0;
            replicationPerfWindowRuntimeUpdateMs = 0d;
            replicationPerfWindowRuntimeUpdateMaxMs = 0d;
            Array.Clear(ReplicationPerfWindowSectionMs, 0, ReplicationPerfWindowSectionMs.Length);
        }

        private static void RecordReplicationRuntimeUpdateCost(double elapsedMs)
        {
            replicationPerfWindowRuntimeUpdateMs += elapsedMs;
            if (elapsedMs > replicationPerfWindowRuntimeUpdateMaxMs)
            {
                replicationPerfWindowRuntimeUpdateMaxMs = elapsedMs;
            }
        }
    }
}
