using UnityEngine;
using ExplainableXR;
using Logger = ExplainableXR.Logger;

public class VRVoiceRecorder
{
    private readonly Camera userHeadCam;
    private readonly Logger logger;
    private readonly AoIBayesianFilter aoiFilter;
    private int voiceActionID;

    public VRVoiceRecorder(Camera userHeadCam, Logger logger, AoIBayesianFilter aoiFilter)
    {
        this.userHeadCam = userHeadCam;
        this.logger = logger;
        this.aoiFilter = aoiFilter;
    }

    public int OnTriggerPressed(ReferentMetadata referentMetadata)
    {
        var observedAction = new ObservedAction(UserActionType.Voice, IntentType.Communication,
            "User starts a conversation", null);
        if (aoiFilter.GetIntentConfidence(IntentType.Communication) >= aoiFilter.aoiThreshold)
        {
            voiceActionID = logger.LogContinuousActionBegin("Voice", "PostDefined",
                ActionTriggerSource.Microphone, userHeadCam.transform, null, null,
                ReferentType.Virtual, ActionContextType.Virtual, LogRecordType.Voice);
        }
        aoiFilter.ProcessObservedAction(observedAction);
        return 0;
    }

    public int OnTriggerHeld((ReferentMetadata referentMetadata, bool record) args)
    {
        if (args.record)
        {
            var observedAction = new ObservedAction(UserActionType.Voice, IntentType.Communication,
                "User speaks continuously", null);
            if (aoiFilter.GetIntentConfidence(IntentType.Communication) >= aoiFilter.aoiThreshold)
                logger.LogContinuousActionContinue(voiceActionID);
            aoiFilter.ProcessObservedAction(observedAction);
        }
        return 0;
    }

    public int OnTriggerReleased(ReferentMetadata referentMetadata)
    {
        logger.LogContinuousActionEnd(voiceActionID);
        return 0;
    }
}
