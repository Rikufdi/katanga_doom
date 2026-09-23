using UnityEngine;
using UnityEngine.InputSystem;

// All VR controller input for Katanga, using the Unity Input System on top of OpenXR.
//
// This replaces the old SteamVR Input actions (actions.json + per-controller binding
// files).  Everything is bound by generic XR usages instead of per-controller paths,
// so the same bindings work on any OpenXR runtime (SteamVR, VDXR/Virtual Desktop,
// Oculus/Meta, WMR) and any controller that has an interaction profile enabled.
//
// The layout mirrors the old SteamVR default bindings:
//
//  Right stick/trackpad:  up/down  = screen farther/nearer
//                         left/right = flatten/curve screen
//  Left stick/trackpad:   up/down  = screen higher/lower
//                         left/right = screen smaller/bigger
//  Right trigger:         recenter screen
//  Left trigger:          cycle environment
//  Left A/X or menu:      cycle sharpening/upscaling
//  Right A or menu:       show/hide controller hints
//  Right grip (hold):     pause slideshow
//  Left grip:             next slide
//
// Keyboard and gamepad are still handled by the legacy InputManager axes in the
// individual scripts, so they only act when Katanga has focus.

public static class KatangaInput
{
    public enum Direction { None, North, South, East, West }

    // Stick deflection needed before a direction counts as pressed.  The old SteamVR
    // dpad bindings used a similar threshold for the 'touch' sub mode.
    const float dpadThreshold = 0.6f;

    static InputActionMap map;

    public static InputAction LeftStick { get; private set; }
    public static InputAction RightStick { get; private set; }
    public static InputAction LeftStickClick { get; private set; }
    public static InputAction RightStickClick { get; private set; }

    public static InputAction Recenter { get; private set; }
    public static InputAction CycleEnvironment { get; private set; }
    public static InputAction ToggleSharpening { get; private set; }
    public static InputAction ToggleHints { get; private set; }
    public static InputAction Pause { get; private set; }
    public static InputAction Skip { get; private set; }

    // Build and enable the actions on first use.  Safe to call from any Awake/OnEnable.
    public static void Enable()
    {
        if (map == null)
            Build();
        map.Enable();
    }

    static void Build()
    {
        map = new InputActionMap("Katanga");

        LeftStick = map.AddAction("LeftStick", InputActionType.Value, "<XRController>{LeftHand}/{Primary2DAxis}");
        RightStick = map.AddAction("RightStick", InputActionType.Value, "<XRController>{RightHand}/{Primary2DAxis}");
        LeftStickClick = map.AddAction("LeftStickClick", InputActionType.Button, "<XRController>{LeftHand}/{Primary2DAxisClick}");
        RightStickClick = map.AddAction("RightStickClick", InputActionType.Button, "<XRController>{RightHand}/{Primary2DAxisClick}");

        Recenter = map.AddAction("Recenter", InputActionType.Button, "<XRController>{RightHand}/{TriggerButton}");
        CycleEnvironment = map.AddAction("CycleEnvironment", InputActionType.Button, "<XRController>{LeftHand}/{TriggerButton}");

        // A/X style buttons, with the menu button as a fallback for Vive wands and WMR.
        ToggleSharpening = map.AddAction("ToggleSharpening", InputActionType.Button, "<XRController>{LeftHand}/{PrimaryButton}");
        ToggleSharpening.AddBinding("<XRController>{LeftHand}/{MenuButton}");
        ToggleHints = map.AddAction("ToggleHints", InputActionType.Button, "<XRController>{RightHand}/{PrimaryButton}");
        ToggleHints.AddBinding("<XRController>{RightHand}/{MenuButton}");

        Pause = map.AddAction("Pause", InputActionType.Button, "<XRController>{RightHand}/{GripButton}");
        Skip = map.AddAction("Skip", InputActionType.Button, "<XRController>{LeftHand}/{GripButton}");
    }

    // Turn a stick or trackpad into the 4-way dpad that the SteamVR bindings used.
    // Trackpads (Vive wands) only count when clicked, otherwise just resting a thumb
    // on the pad would move the screen.  Sticks count on deflection.

    public static Direction LeftDirection() { return ReadDirection(LeftStick, LeftStickClick); }
    public static Direction RightDirection() { return ReadDirection(RightStick, RightStickClick); }

    static Direction ReadDirection(InputAction stick, InputAction click)
    {
        if (stick == null)
            return Direction.None;

        Vector2 v = stick.ReadValue<Vector2>();
        if (v.magnitude < dpadThreshold)
            return Direction.None;

        if (IsTrackpad(stick) && !click.IsPressed())
            return Direction.None;

        if (Mathf.Abs(v.y) >= Mathf.Abs(v.x))
            return v.y > 0 ? Direction.North : Direction.South;
        return v.x > 0 ? Direction.East : Direction.West;
    }

    static bool IsTrackpad(InputAction stick)
    {
        InputControl control = stick.activeControl;
        if (control == null)
            return false;
        string name = control.name.ToLowerInvariant();
        return name.Contains("trackpad") || name.Contains("touchpad");
    }
}
