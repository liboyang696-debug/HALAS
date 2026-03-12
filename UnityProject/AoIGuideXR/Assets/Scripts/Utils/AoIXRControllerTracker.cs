using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using ExplainableXR;

public enum InteractionType
{
    None,
    Direct,
    Indirect
}

public class ReferentMetadata
{
    public GameObject Gobj;
    public InteractionType interactionType;
    public Vector2 joystickInput;
}

public class AoIXRControllerTracker : AoIXRTracker
{
    private InputAction inputAction;

    // Callbacks for controller input lifecycle: Pressed → Held → Released
    private Func<ReferentMetadata, int> controllerPressed;
    private Func<(ReferentMetadata, bool), int> controllerPressHeld;
    private Func<ReferentMetadata, int> controllerReleased;

    private float pressThreshold;
    private float joystickThreshold;
    private float holdInterval; // Minimum interval between hold-event recordings (in seconds)
    private bool isPressed;
    private bool isJoystickMove;
    private float lastHeldTime;
    private ReferentMetadata referentMetadata = new();

    public AoIXRControllerTracker(
        MonoBehaviour monoBehaviour,
        InputAction inputAction,
        Func<ReferentMetadata, int> controllerPressed,
        Func<(ReferentMetadata, bool), int> controllerPressHeld,
        Func<ReferentMetadata, int> controllerReleased,
        float actionInvokeInterval = 0.05f,
        float pressThreshold = 0.25f,
        float joystickThreshold = 0.25f,
        float holdInterval = 1.0f)
        : base(monoBehaviour, actionInvokeInterval)
    {
        this.inputAction = inputAction;
        this.controllerPressed = controllerPressed;
        this.controllerPressHeld = controllerPressHeld;
        this.controllerReleased = controllerReleased;

        this.pressThreshold = pressThreshold;
        this.holdInterval = holdInterval;
        this.joystickThreshold = joystickThreshold;
        isPressed = false;
        lastHeldTime = 0f;
    }

    protected override void InvokeTrackEvent()
    {
        // Determine input type by control type or action name
        bool isJoystickAction = inputAction.expectedControlType == "Vector2" ||
                                inputAction.name.Contains("2DAxis") ||
                                inputAction.name.Contains("Joystick") ||
                                inputAction.name.Contains("thumbstick");
        Debug.Log($"isJoystickAction: {isJoystickAction}   {inputAction.name.Contains("2DAxis")}     {inputAction.name.Contains("Joystick")}     {inputAction.name.Contains("thumbstick")}");
        if (isJoystickAction)
            HandleJoystickInput();
        else
            HandleTriggerInput();
    }

    private void HandleJoystickInput()
    {
        Vector2 joystickValue = inputAction.ReadValue<Vector2>();
        referentMetadata.joystickInput = joystickValue;
        float magnitude = joystickValue.magnitude;

        if (!isJoystickMove && magnitude > joystickThreshold)
        {
            isJoystickMove = true;
            lastHeldTime = Time.time;
            controllerPressed.Invoke(referentMetadata);
        }

        if (isJoystickMove)
        {
            bool isHoldIntervalReached = Time.time - lastHeldTime >= holdInterval;
            if (isHoldIntervalReached)
                lastHeldTime = Time.time;

            controllerPressHeld.Invoke((referentMetadata, isHoldIntervalReached));

            if (magnitude <= joystickThreshold)
            {
                isJoystickMove = false;
                controllerReleased.Invoke(referentMetadata);
            }
        }
    }

    private void HandleTriggerInput()
    {
        float triggerValue = inputAction.ReadValue<float>();
        referentMetadata.joystickInput = Vector2.zero;

        if (!isPressed && triggerValue > pressThreshold)
        {
            isPressed = true;
            lastHeldTime = Time.time;
            controllerPressed.Invoke(referentMetadata);
        }

        if (isPressed)
        {
            bool isHoldIntervalReached = Time.time - lastHeldTime >= holdInterval;
            if (isHoldIntervalReached)
                lastHeldTime = Time.time;

            controllerPressHeld.Invoke((referentMetadata, isHoldIntervalReached));

            if (triggerValue <= pressThreshold)
            {
                isPressed = false;
                controllerReleased.Invoke(referentMetadata);
            }
        }
    }
}
