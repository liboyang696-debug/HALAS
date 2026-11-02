using System;
using UnityEngine;
using ExplainableXR;
using Logger = ExplainableXR.Logger;

public class VRVoiceRecorder
{
    private readonly Camera userHeadCam;
    private readonly Logger logger;
    private readonly AoIBayesianFilter aoiFilter;
    private int voiceActionID;
    private string voiceBehaviorId = string.Empty;
    private float voiceStartTime;

    public VRVoiceRecorder(Camera userHeadCam, Logger logger, AoIBayesianFilter aoiFilter)
    {
        this.userHeadCam = userHeadCam;
        this.logger = logger;
        this.aoiFilter = aoiFilter;
    }

    public int OnTriggerPressed(ReferentMetadata referentMetadata)
    {
        voiceBehaviorId = $"vr-voice-{Guid.NewGuid():N}";
        voiceStartTime = Time.unscaledTime;
        var observedAction = new ObservedAction(ActionEvidenceType.VoiceActive,
            "User starts speaking", null, 0, voiceBehaviorId,
            BehaviorEventPhase.Begin, TriggerModality.Voice,
            CurrentBehaviorState.Speaking, IntentType.Communication,
            BehaviorTemporalType.Continuous);
        observedAction.sequenceNumber = 0;
        AoIDecision decision = aoiFilter.ObserveEvidence(observedAction);
        if (decision.shouldUpload)
        {
            voiceActionID = StartVoiceUpload();
            AoIUBRMetadataBridge.Register(voiceActionID, observedAction, decision);
        }
        return voiceActionID;
    }

    public int OnTriggerHeld((ReferentMetadata referentMetadata, bool record) args)
    {
        if (!string.IsNullOrEmpty(voiceBehaviorId))
        {
            var observedAction = new ObservedAction(ActionEvidenceType.VoiceActive,
                "User speaks continuously", null, 0, voiceBehaviorId,
                BehaviorEventPhase.Continue, TriggerModality.Voice,
                CurrentBehaviorState.Speaking, IntentType.Communication,
                BehaviorTemporalType.Continuous);
            observedAction.elapsedSeconds = Mathf.Max(0f, Time.unscaledTime - voiceStartTime);
            AoIDecision decision = aoiFilter.ObserveEvidence(observedAction);
            if (decision.sendCancel)
            {
                CancelUpload(voiceActionID);
                voiceActionID = 0;
            }
            else if (decision.confirmed && !decision.rejected)
            {
                bool startsLate = voiceActionID <= 0;
                // Confirmation can arrive after the local voice behavior starts.
                if (startsLate)
                    voiceActionID = StartVoiceUpload();
                else if (args.record)
                    logger.LogContinuousActionContinue(voiceActionID);

                if (startsLate || args.record)
                {
                    AoIUBRMetadataBridge.Register(voiceActionID, observedAction, decision,
                        startsLate ? BehaviorEventPhase.Begin : BehaviorEventPhase.Continue);
                }
            }
        }
        return voiceActionID;
    }

    public int OnTriggerReleased(ReferentMetadata referentMetadata)
    {
        if (!string.IsNullOrEmpty(voiceBehaviorId))
        {
            var end = new ObservedAction(ActionEvidenceType.VoiceActive,
                "User stops speaking", null, 0, voiceBehaviorId,
                BehaviorEventPhase.End, TriggerModality.Voice,
                CurrentBehaviorState.Speaking, IntentType.Communication,
                BehaviorTemporalType.Continuous);
            end.elapsedSeconds = Mathf.Max(0f, Time.unscaledTime - voiceStartTime);
            AoIDecision decision = aoiFilter.ObserveEvidence(end);
            if (decision.sendCancel && voiceActionID > 0)
                CancelUpload(voiceActionID);
            else if (decision.shouldUpload)
            {
                if (voiceActionID <= 0)
                {
                    voiceActionID = StartVoiceUpload();
                    AoIUBRMetadataBridge.Register(voiceActionID, end, decision,
                        BehaviorEventPhase.Begin);
                }
                logger.LogContinuousActionEnd(voiceActionID);
                AoIUBRMetadataBridge.Register(voiceActionID, end, decision);
            }
        }
        voiceBehaviorId = string.Empty;
        voiceActionID = 0;
        return 0;
    }

    private int StartVoiceUpload()
    {
        return logger.LogContinuousActionBegin("Voice", "PostDefined",
            ActionTriggerSource.Microphone, userHeadCam.transform, null, null,
            ReferentType.Virtual, ActionContextType.Virtual, LogRecordType.Voice);
    }

    private void CancelUpload(int actionId)
    {
        if (actionId <= 0) return;
        logger.DiscardContinuousAction(actionId);
        if (UBRParserAndSender.Instance != null)
            UBRParserAndSender.Instance.CancelAoIAction(actionId);
    }
}
